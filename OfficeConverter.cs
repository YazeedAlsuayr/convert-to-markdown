// Word, PowerPoint and Excel (.docx, .pptx, .xlsx) -> Markdown.
// These files are zip folders of XML, so they are read directly: no Office, no add-ons.
// Text, headings, lists, tables, links and speaker notes come from the file itself (exact);
// the text inside pictures is read by Windows OCR.

using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace ConvertToMarkdown;

sealed class OfficeConverter : IDisposable
{
    public static readonly string[] Types = { ".docx", ".docm", ".pptx", ".pptm", ".xlsx", ".xlsm" };

    static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    static readonly XNamespace P = "http://schemas.openxmlformats.org/presentationml/2006/main";
    static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    static readonly XNamespace Rel = "http://schemas.openxmlformats.org/package/2006/relationships";
    static readonly XNamespace V = "urn:schemas-microsoft-com:vml";

    readonly ZipArchive zip;
    readonly IProgress<Step> progress;
    readonly CancellationToken ct;
    int picturesRead;
    bool aiUsed;

    // Word state
    Dictionary<string, string> wordRels = new();
    readonly Dictionary<string, string> styleNames = new();
    readonly Dictionary<string, string> numToAbstract = new();
    readonly Dictionary<(string, int), string> listFormats = new();
    readonly Dictionary<string, XElement> styleNumPr = new();
    readonly Dictionary<string, string> styleBasedOn = new();
    readonly Dictionary<(string, int), int> listCounters = new();

    /// <summary>The numbering a paragraph style gives (looking through the styles it is based on).</summary>
    XElement StyleNumPr(string styleId)
    {
        for (int depth = 0; styleId != null && depth < 10; depth++)
        {
            if (styleNumPr.TryGetValue(styleId, out var np)) return np;
            styleBasedOn.TryGetValue(styleId, out styleId);
        }
        return null;
    }

    OfficeConverter(string path, IProgress<Step> progress, CancellationToken ct)
    {
        this.progress = progress;
        this.ct = ct;
        try { zip = ZipFile.OpenRead(path); }
        catch (InvalidDataException e) { throw new FriendlyException(WhyNotZip(path), e); }
    }

    /// <summary>
    /// A .docx/.pptx/.xlsx is a zip folder. When it isn't one: a password-protected Office file (Office then saves
    /// it in an older "box" format with an EncryptionInfo part), an old .doc/.ppt/.xls renamed, or a damaged file.
    /// </summary>
    static string WhyNotZip(string path)
    {
        byte[] head;
        try
        {
            using var f = File.OpenRead(path);
            head = new byte[(int)Math.Min(f.Length, 1 << 20)];
            f.ReadExactly(head);
        }
        catch { head = Array.Empty<byte>(); }
        bool oleBox = head.Length >= 8 && head[0] == 0xD0 && head[1] == 0xCF && head[2] == 0x11 && head[3] == 0xE0;
        if (oleBox && Encoding.Unicode.GetString(head).Contains("EncryptionInfo"))
            return "Password-protected file. Remove the password in Office and try again.";
        if (oleBox)
            return "Old Office file. Save it as .docx, .pptx or .xlsx first.";
        return "This file is damaged and can't be read.";
    }

    public void Dispose() => zip.Dispose();

    public static async Task<string> ConvertAsync(string path, IProgress<Step> progress, CancellationToken ct)
    {
        using var c = new OfficeConverter(path, progress, ct);
        string ext = Path.GetExtension(path).ToLowerInvariant();
        progress.Report(new Step("Opening the file", 0));
        // the file opened fine: start the AI now if it has pictures (a file without pictures doesn't need it)
        if (c.zip.Entries.Any(e => e.FullName.Contains("/media/", StringComparison.OrdinalIgnoreCase))) Converter.WarmUpAi(ct);
        string body;
        try
        {
            body = ext switch
            {
                ".docx" or ".docm" => await c.WordAsync(),
                ".pptx" or ".pptm" => await c.PowerPointAsync(),
                _ => await c.ExcelAsync()
            };
        }
        catch (Exception e) when (e is System.Xml.XmlException or InvalidDataException)
        {
            Log.Write("Office read: " + e);
            throw new FriendlyException("This file is damaged and can't be read.", e);
        }
        var md = new StringBuilder();
        md.Append(Converter.Mark).Append("\n\n# ").Append(Path.GetFileNameWithoutExtension(path)).Append("\n\n");
        md.Append(c.aiUsed
            ? $"> Text comes from the file itself (exact). Pictures are {(Settings.Describe ? "described and " : "")}read by the built-in AI ({Ai.ModelName}) and CAN contain errors.\n\n"
            : c.picturesRead > 0
            ? "> Text comes from the file itself (exact). Text inside pictures is read by the quick built-in reader and CAN contain errors.\n\n"
            : "> Text comes from the file itself (exact).\n\n");
        md.Append(body.Trim()).Append('\n');
        progress.Report(new Step("Saving", 1));
        return md.ToString();
    }

    // ======================= package helpers =======================
    XDocument Load(string part)
    {
        var e = part == null ? null : zip.GetEntry(part);
        if (e == null) return null;
        using var s = e.Open();
        // keep a text piece that is only a space ("Diagnostic" + " " + "Criteria" in PowerPoint, which has no
        // xml:space="preserve"); drop only the layout whitespace between tags
        var doc = XDocument.Load(s, LoadOptions.PreserveWhitespace);
        doc.DescendantNodes().OfType<XText>()
            .Where(t => string.IsNullOrWhiteSpace(t.Value) && t.Parent != null && t.Parent.Elements().Any())
            .ToList().ForEach(t => t.Remove());
        return doc;
    }

    /// <summary>Relationship id -> full part path (or the URL for external links).</summary>
    Dictionary<string, string> Rels(string part)
    {
        string dir = DirOf(part);
        string name = part.Substring(dir.Length).TrimStart('/');
        var doc = Load((dir.Length > 0 ? dir + "/" : "") + "_rels/" + name + ".rels");
        var map = new Dictionary<string, string>();
        if (doc?.Root == null) return map;
        foreach (var r in doc.Root.Elements(Rel + "Relationship"))
        {
            string id = (string)r.Attribute("Id"), target = (string)r.Attribute("Target"), mode = (string)r.Attribute("TargetMode");
            if (id == null || target == null) continue;
            map[id] = mode == "External" ? target : Resolve(dir, target);
        }
        return map;
    }

    static string DirOf(string part)
    {
        int i = part.LastIndexOf('/');
        return i < 0 ? "" : part.Substring(0, i);
    }

    static string Resolve(string dir, string target)
    {
        if (target.StartsWith("/")) return target.TrimStart('/');
        var parts = dir.Length > 0 ? dir.Split('/').ToList() : new List<string>();
        foreach (var seg in target.Split('/'))
        {
            if (seg == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
            else if (seg != "." && seg.Length > 0) parts.Add(seg);
        }
        return string.Join("/", parts);
    }

    static string MarkdownTable(List<List<string>> rows)
    {
        rows = rows.Where(r => r.Any(c => c.Trim().Length > 0)).ToList();
        if (rows.Count == 0) return "";
        int cols = rows.Max(r => r.Count);
        var sb = new StringBuilder();
        for (int i = 0; i < rows.Count; i++)
        {
            var cells = rows[i].Concat(Enumerable.Repeat("", cols - rows[i].Count)).Select(c => c.Length == 0 ? " " : c);
            sb.Append("| ").Append(string.Join(" | ", cells)).Append(" |\n");
            if (i == 0) sb.Append('|').Append(string.Concat(Enumerable.Repeat(" --- |", cols))).Append('\n');
        }
        return sb.ToString().TrimEnd();
    }

    static string Cell(string s) => s.Replace("|", "\\|").Replace("\r", "").Replace("\n", "<br>").Trim();

    // ======================= pictures =======================
    static readonly HttpClient web = new() { Timeout = TimeSpan.FromSeconds(20) };

    static bool IsWebLink(string target) =>
        target != null && (target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || target.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The picture's Markdown block; null when the picture was already shown (logos repeated on every slide).
    /// <paramref name="target"/> is a part inside the file, or a web link for pictures that are only linked.
    /// </summary>
    async Task<string> PictureAsync(string target)
    {
        ct.ThrowIfCancellationRequested();
        if (target == null || seen.Contains(target)) return null;
        seen.Add(target);
        byte[] bytes;
        if (IsWebLink(target))
        {
            if (!Settings.Describe) return $"*(linked picture: {target})*";
            bytes = await DownloadAsync(target);
            if (bytes == null) return $"*(linked picture: {target}, couldn't be downloaded)*";
        }
        else
        {
            var entry = zip.GetEntry(target);
            string ext = Path.GetExtension(target).ToLowerInvariant();
            if (entry == null || ext is ".emf" or ".wmf" or ".svg") return "*(picture)*";
            using var ms = new MemoryStream();
            using (var s = entry.Open()) s.CopyTo(ms);
            bytes = ms.ToArray();
        }
        return await ReadPictureAsync(bytes, target);
    }

    readonly HashSet<string> seen = new();

    async Task<byte[]> DownloadAsync(string url)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd("ConvertToMarkdown/2.0");
            using var resp = await web.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode || (resp.Content.Headers.ContentLength ?? 0) > 20_000_000) return null;
            return await resp.Content.ReadAsByteArrayAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e)
        {
            Log.Write("linked picture " + url + ": " + e.Message);
            return null;
        }
    }

    async Task<string> ReadPictureAsync(byte[] bytes, string name)
    {
        try
        {
            using var ms = new MemoryStream(bytes);
            var dec = await BitmapDecoder.CreateAsync(ms.AsRandomAccessStream());
            if (dec.PixelWidth < 40 || dec.PixelHeight < 25) return "*(small picture)*";
            double max = OcrEngine.MaxImageDimension;
            double sc = Math.Min(1.0, max / Math.Max(dec.PixelWidth, dec.PixelHeight));
            var tr = new BitmapTransform
            {
                ScaledWidth = (uint)Math.Max(1, dec.PixelWidth * sc),
                ScaledHeight = (uint)Math.Max(1, dec.PixelHeight * sc),
                InterpolationMode = BitmapInterpolationMode.Fant
            };
            using var bmp = await dec.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                tr, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);
            picturesRead++;

            bool describe = Settings.Describe;
            if (Ai.Enabled)
            {
                try
                {
                    if (await Ai.EnsureRunningAsync(null, ct))
                    {
                        string answer = await Ai.AskAsync(describe ? Ai.PicturePrompt : Ai.PictureTextPrompt,
                                                          await Converter.PngAsync(bmp, 1280), 900, false, ct);
                        aiUsed = true;
                        var (d, t) = Ai.SplitPicture(answer, describe);
                        if (d.Length == 0 && t.Length == 0) return "*(picture)*";
                        var sb = new StringBuilder("**Picture (AI - verify):**");
                        if (d.Length > 0) sb.Append("\n\n*").Append(d).Append('*');
                        if (t.Length > 0) sb.Append("\n\n").Append(t);
                        return sb.ToString();
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception e) { Log.Write("AI: " + e.Message); }
            }
            string text = Converter.CleanOcr(await Converter.OcrAsync(bmp));
            return text.Length == 0 ? "*(picture)*" : "**Picture text (quick reader - verify):**\n\n" + text;
        }
        catch (Exception e)
        {
            Log.Write("office picture " + name + ": " + e.Message);
            return "*(picture)*";
        }
    }

    // ======================= Word =======================
    async Task<string> WordAsync()
    {
        const string main = "word/document.xml";
        var doc = Load(main) ?? throw new InvalidDataException("This Word file has no document inside.");
        wordRels = Rels(main);

        var styles = Load("word/styles.xml");
        if (styles?.Root != null)
            foreach (var st in styles.Root.Elements(W + "style"))
            {
                string id = (string)st.Attribute(W + "styleId"), name = (string)st.Element(W + "name")?.Attribute(W + "val");
                if (id == null) continue;
                if (name != null) styleNames[id] = name.ToLowerInvariant();
                // list styles ("List Number", "List Bullet 2") keep their numbering in the style, not in each paragraph
                var snp = st.Element(W + "pPr")?.Element(W + "numPr");
                if (snp != null) styleNumPr[id] = snp;
                string based = (string)st.Element(W + "basedOn")?.Attribute(W + "val");
                if (based != null) styleBasedOn[id] = based;
            }
        var numbering = Load("word/numbering.xml");
        if (numbering?.Root != null)
        {
            foreach (var an in numbering.Root.Elements(W + "abstractNum"))
            {
                string id = (string)an.Attribute(W + "abstractNumId") ?? "";
                foreach (var lvl in an.Elements(W + "lvl"))
                    listFormats[(id, (int?)lvl.Attribute(W + "ilvl") ?? 0)] = (string)lvl.Element(W + "numFmt")?.Attribute(W + "val") ?? "bullet";
            }
            foreach (var n in numbering.Root.Elements(W + "num"))
                numToAbstract[(string)n.Attribute(W + "numId") ?? ""] = (string)n.Element(W + "abstractNumId")?.Attribute(W + "val");
        }

        var body = doc.Root?.Element(W + "body");
        if (body == null) return "";
        var blocks = Blocks(body).ToList();
        var md = new StringBuilder();
        string prevKind = "";
        for (int i = 0; i < blocks.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (i % 20 == 0) progress.Report(new Step($"Reading the document  ·  {i * 100 / Math.Max(1, blocks.Count)}%", (double)i / blocks.Count));
            var b = blocks[i];
            string text, kind;
            if (b.Name == W + "tbl") { text = await TableAsync(b); kind = "table"; }
            else { (text, kind) = await ParagraphAsync(b); }
            if (text.Trim().Length == 0) continue;
            // list items stay together; everything else is separated by a blank line
            md.Append(kind == "list" && prevKind == "list" ? "\n" : "\n\n").Append(text.TrimEnd());
            prevKind = kind;
        }
        return md.ToString();
    }

    IEnumerable<XElement> Blocks(XElement parent)
    {
        foreach (var e in parent.Elements())
        {
            if (e.Name == W + "p" || e.Name == W + "tbl") yield return e;
            else if (e.Name == W + "sectPr" || e.Name == W + "del") continue;
            else foreach (var x in Blocks(e)) yield return x;       // content controls, inserted text, custom XML
        }
    }

    async Task<(string text, string kind)> ParagraphAsync(XElement p)
    {
        var ppr = p.Element(W + "pPr");
        string styleId = (string)ppr?.Element(W + "pStyle")?.Attribute(W + "val");
        string style = styleId != null && styleNames.TryGetValue(styleId, out var nm) ? nm : (styleId ?? "").ToLowerInvariant();
        int heading = style == "title" ? 1 : 0;
        var hm = Regex.Match(style, @"^heading\s*(\d)");
        if (hm.Success) heading = int.Parse(hm.Groups[1].Value);
        if (heading == 0 && int.TryParse((string)ppr?.Element(W + "outlineLvl")?.Attribute(W + "val"), out int ol) && ol < 6) heading = ol + 1;

        var (text, pics) = await InlineAsync(p, plain: heading > 0);
        text = text.Trim();
        string kind = "text";
        var sb = new StringBuilder();
        if (text.Length > 0)
        {
            if (heading > 0)
            {
                // the document's own title is the main heading; "Heading 1" sits one level below it
                sb.Append(new string('#', style == "title" ? 1 : Math.Min(heading + 1, 6))).Append(' ').Append(text);
                kind = "heading";
            }
            else
            {
                var numPr = ppr?.Element(W + "numPr");
                var styleNp = StyleNumPr(styleId);
                string numId = (string)numPr?.Element(W + "numId")?.Attribute(W + "val") ?? (string)styleNp?.Element(W + "numId")?.Attribute(W + "val");
                if (numId == "0") numId = null;                          // numbering switched off for this paragraph
                if (numId != null || (numPr == null && style.StartsWith("list")))
                {
                    int? ownLvl = (int?)numPr?.Element(W + "ilvl")?.Attribute(W + "val");
                    int numLvl = ownLvl ?? (int?)styleNp?.Element(W + "ilvl")?.Attribute(W + "val") ?? 0;   // level inside the numbering
                    int ilvl = numLvl;
                    // "List Bullet 2", "List Number 3": the number in the style name is the indent level
                    var lm = Regex.Match(style, @"^list(?: bullet| number| continue)?\s+(\d)$");
                    if (ownLvl == null && lm.Success) ilvl = Math.Max(ilvl, int.Parse(lm.Groups[1].Value) - 1);
                    bool bullet = true;
                    if (numId != null && numToAbstract.TryGetValue(numId, out var abs) && abs != null
                        && listFormats.TryGetValue((abs, numLvl), out var fmt))
                        bullet = fmt is "bullet" or "none";
                    string marker = "- ";
                    if (!bullet)
                    {
                        // count 1, 2, 3 per list and level; a higher level starts the deeper levels again
                        var key = (numId ?? style, ilvl);
                        listCounters[key] = listCounters.GetValueOrDefault(key) + 1;
                        foreach (var k in listCounters.Keys.Where(k => k.Item1 == key.Item1 && k.Item2 > ilvl).ToList()) listCounters.Remove(k);
                        marker = listCounters[key] + ". ";
                    }
                    sb.Append(new string(' ', 2 * ilvl)).Append(marker).Append(text);
                    kind = "list";
                }
                else sb.Append(text);
            }
        }
        foreach (var pic in pics)
        {
            if (sb.Length > 0) sb.Append("\n\n");
            sb.Append(pic);
            if (kind == "list") kind = "text";
        }
        return (sb.ToString(), kind);
    }

    async Task<(string text, List<string> pictures)> InlineAsync(XElement p, bool plain)
    {
        var segs = new List<(string t, bool b, bool i, string link)>();
        var pics = new List<string>();

        async Task Walk(XElement parent, string link)
        {
            foreach (var e in parent.Elements())
            {
                string n = e.Name.LocalName;
                if (e.Name == W + "r") await Run(e, link);
                else if (e.Name == W + "hyperlink")
                {
                    string id = (string)e.Attribute(R + "id");
                    string url = id != null && wordRels.TryGetValue(id, out var u) && u.Contains(':') ? u : null;
                    await Walk(e, url ?? link);
                }
                else if (n is "oMath" or "oMathPara")
                    segs.Add((string.Concat(e.Descendants().Where(d => d.Name.LocalName == "t").Select(d => d.Value)), false, false, link));
                else if (e.Name == W + "pPr" || e.Name == W + "del" || e.Name == W + "moveFrom") continue;
                else await Walk(e, link);
            }
        }

        async Task Drawing(XElement c)
        {
            if (c.Name.LocalName == "AlternateContent")
            {
                var choice = c.Elements().FirstOrDefault(x => x.Name.LocalName == "Choice");
                if (choice != null) foreach (var x in choice.Elements()) await Drawing(x);
                return;
            }
            var blip = c.Descendants(A + "blip").FirstOrDefault();
            string id = (string)blip?.Attribute(R + "embed") ?? (string)blip?.Attribute(R + "link")
                     ?? (string)c.Descendants(V + "imagedata").FirstOrDefault()?.Attribute(R + "id");
            if (id != null && wordRels.TryGetValue(id, out var target))
            {
                var t = await PictureAsync(target);
                if (t != null) pics.Add(t);
            }
            foreach (var box in c.Descendants(W + "txbxContent"))          // text boxes
                foreach (var tp in box.Elements(W + "p"))
                {
                    var (tt, _) = await InlineAsync(tp, true);
                    if (tt.Trim().Length > 0) pics.Add(tt.Trim());
                }
        }

        async Task Run(XElement r, string link)
        {
            var rPr = r.Element(W + "rPr");
            bool b = On(rPr?.Element(W + "b")), it = On(rPr?.Element(W + "i"));
            var sb = new StringBuilder();
            foreach (var c in r.Elements())
            {
                if (c.Name == W + "t") sb.Append(c.Value);
                else if (c.Name == W + "tab" || c.Name == W + "br" || c.Name == W + "cr") sb.Append(' ');
                else if (c.Name == W + "noBreakHyphen") sb.Append('-');
                else if (c.Name == W + "drawing" || c.Name == W + "pict" || c.Name == W + "object" || c.Name.LocalName == "AlternateContent")
                    await Drawing(c);
            }
            if (sb.Length > 0) segs.Add((sb.ToString(), b && !plain, it && !plain, link));
        }

        await Walk(p, null);

        var merged = new List<(string t, bool b, bool i, string link)>();
        foreach (var s in segs)
        {
            if (merged.Count > 0 && merged[^1].b == s.b && merged[^1].i == s.i && merged[^1].link == s.link)
                merged[^1] = (merged[^1].t + s.t, s.b, s.i, s.link);
            else merged.Add(s);
        }
        var outp = new StringBuilder();
        foreach (var s in merged) outp.Append(Format(s.t, s.b, s.i, s.link));
        return (outp.ToString(), pics);
    }

    static string Format(string t, bool bold, bool italic, string link)
    {
        if (t.Trim().Length == 0) return t;
        string lead = t.Substring(0, t.Length - t.TrimStart().Length), trail = t.Substring(t.TrimEnd().Length), core = t.Trim();
        if (link != null) core = $"[{core}]({link})";
        if (italic) core = "*" + core + "*";
        if (bold) core = "**" + core + "**";
        return lead + core + trail;
    }

    static bool On(XElement e)
    {
        if (e == null) return false;
        string v = (string)e.Attribute(W + "val");
        return v == null || !(v == "0" || v == "false" || v == "off");
    }

    async Task<string> TableAsync(XElement tbl)
    {
        var rows = new List<List<string>>();
        foreach (var tr in tbl.Elements(W + "tr"))
        {
            var row = new List<string>();
            foreach (var tc in tr.Elements(W + "tc"))
            {
                var parts = new List<string>();
                foreach (var blk in Blocks(tc))
                {
                    if (blk.Name == W + "p") parts.Add((await ParagraphAsync(blk)).text);
                    else parts.Add(string.Join(" ", blk.Descendants(W + "t").Select(t => t.Value)));   // a table inside a cell
                }
                row.Add(Cell(string.Join("\n", parts.Where(x => x.Trim().Length > 0))));
                int span = (int?)tc.Element(W + "tcPr")?.Element(W + "gridSpan")?.Attribute(W + "val") ?? 1;
                for (int k = 1; k < span; k++) row.Add("");
            }
            rows.Add(row);
        }
        return MarkdownTable(rows);
    }

    // ======================= PowerPoint =======================
    async Task<string> PowerPointAsync()
    {
        const string main = "ppt/presentation.xml";
        var pres = Load(main) ?? throw new InvalidDataException("This PowerPoint file has no presentation inside.");
        var rels = Rels(main);
        var slides = pres.Root?.Element(P + "sldIdLst")?.Elements(P + "sldId")
            .Select(s => (string)s.Attribute(R + "id"))
            .Where(id => id != null && rels.ContainsKey(id))
            .Select(id => rels[id]).ToList() ?? new List<string>();
        var md = new StringBuilder();
        var hidden = new List<int>();
        for (int i = 0; i < slides.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            progress.Report(new Step($"Slide {i + 1} of {slides.Count}", (double)i / Math.Max(1, slides.Count)));
            string slide = await SlideAsync(slides[i], i + 1);
            if (slide == null) hidden.Add(i + 1);
            else md.Append(slide);
        }
        if (hidden.Count > 0)
        {
            md.Append($"\n\n---\n\n*({(hidden.Count == 1 ? "Slide " : "Slides ")}{string.Join(", ", hidden)} hidden in the slide show, not included.)*\n");
            Log.Write($"PowerPoint: {hidden.Count} hidden slide(s) skipped");
        }
        return md.ToString();
    }

    async Task<string> SlideAsync(string part, int number)
    {
        var doc = Load(part);
        if (doc?.Root == null) return "";
        string show = (string)doc.Root.Attribute("show");
        if (show is "0" or "false") return null;                 // hidden slide
        var rels = Rels(part);
        string title = null;
        var items = new List<(double y, double x, string md)>();
        var tree = doc.Root.Element(P + "cSld")?.Element(P + "spTree");
        if (tree != null) await ShapesAsync(tree, rels, items, t => title ??= t);

        var sb = new StringBuilder();
        sb.Append($"\n\n---\n\n## Slide {number}{(string.IsNullOrWhiteSpace(title) ? "" : ": " + title)}\n");
        foreach (var it in items.OrderBy(i => Math.Round(i.y / 91440.0)).ThenBy(i => i.x))   // 0.1 inch rows, then left to right
            if (it.md.Trim().Length > 0) sb.Append('\n').Append(it.md.TrimEnd()).Append('\n');

        // speaker notes
        string notesPart = rels.Values.FirstOrDefault(v => v.Contains("notesSlide"));
        var notes = Load(notesPart);
        if (notes?.Root != null)
        {
            var lines = new List<string>();
            foreach (var sp in notes.Root.Descendants(P + "sp"))
            {
                string type = (string)sp.Element(P + "nvSpPr")?.Element(P + "nvPr")?.Element(P + "ph")?.Attribute("type");
                if (type != "body") continue;
                foreach (var ap in sp.Descendants(A + "p"))
                {
                    string t = ParaText(ap);
                    if (t.Length > 0) lines.Add(t);
                }
            }
            if (lines.Count > 0) sb.Append("\n### Notes\n\n").Append(string.Join("\n", lines)).Append('\n');
        }
        return sb.ToString();
    }

    async Task ShapesAsync(XElement tree, Dictionary<string, string> rels, List<(double y, double x, string md)> items, Action<string> setTitle)
    {
        foreach (var e in tree.Elements())
        {
            var off = e.Descendants(A + "off").FirstOrDefault();
            double x = (double?)off?.Attribute("x") ?? 0, y = (double?)off?.Attribute("y") ?? 0;

            if (e.Name == P + "sp")
            {
                var ph = e.Element(P + "nvSpPr")?.Element(P + "nvPr")?.Element(P + "ph");
                string type = (string)ph?.Attribute("type");
                var body = e.Element(P + "txBody");
                if (body == null || type is "sldNum" or "dt" or "ftr") continue;
                if (type is "title" or "ctrTitle")
                {
                    string t = string.Join(" ", body.Elements(A + "p").Select(ParaText).Where(s => s.Length > 0));
                    if (t.Length > 0) setTitle(t);
                    continue;
                }
                bool contentPlaceholder = ph != null && (type == null || type is "body" or "obj");
                var lines = new List<string>();
                foreach (var ap in body.Elements(A + "p"))
                {
                    string t = ParaText(ap);
                    if (t.Length == 0) continue;
                    var pPr = ap.Element(A + "pPr");
                    int lvl = (int?)pPr?.Attribute("lvl") ?? 0;
                    bool noBullet = pPr?.Element(A + "buNone") != null;
                    bool bullet = pPr?.Element(A + "buChar") != null || pPr?.Element(A + "buAutoNum") != null;
                    lines.Add((contentPlaceholder && !noBullet) || bullet ? new string(' ', 2 * lvl) + "- " + t : t);
                }
                items.Add((y, x, string.Join("\n", lines)));
            }
            else if (e.Name == P + "grpSp")
                await ShapesAsync(e, rels, items, setTitle);
            else if (e.Name == P + "graphicFrame")
            {
                var tbl = e.Descendants(A + "tbl").FirstOrDefault();
                if (tbl != null)
                {
                    var rows = tbl.Elements(A + "tr").Select(tr => tr.Elements(A + "tc")
                        .Select(tc => Cell(string.Join("\n", tc.Descendants(A + "p").Select(ParaText).Where(s => s.Length > 0)))).ToList()).ToList();
                    items.Add((y, x, MarkdownTable(rows)));
                }
                else if (e.Descendants().FirstOrDefault(d => d.Name.LocalName == "chart") is XElement ch)
                {
                    // the chart keeps a copy of its numbers inside the file: title, kind and values, no AI needed
                    string rid = (string)ch.Attribute(R + "id");
                    items.Add((y, x, rid != null && rels.TryGetValue(rid, out var chartPart) ? ChartText(chartPart, null) : "*(chart)*"));
                }
            }
            else if (e.Name == P + "pic")
            {
                var blip = e.Descendants(A + "blip").FirstOrDefault();
                string id = (string)blip?.Attribute(R + "embed") ?? (string)blip?.Attribute(R + "link");
                if (id != null && rels.TryGetValue(id, out var target))
                {
                    string t = await PictureAsync(target);
                    if (t != null) items.Add((y, x, t));
                }
            }
        }
    }

    static string ParaText(XElement ap)
    {
        var sb = new StringBuilder();
        foreach (var e in ap.Elements())
        {
            if (e.Name == A + "r" || e.Name == A + "fld") sb.Append((string)e.Element(A + "t"));
            else if (e.Name == A + "br") sb.Append(' ');
        }
        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }

    // ======================= Excel =======================
    static readonly XNamespace C = "http://schemas.openxmlformats.org/drawingml/2006/chart";
    static readonly XNamespace Xdr = "http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing";

    static bool IsTrue(XAttribute a) => a != null && ((string)a == "1" || ((string)a).Equals("true", StringComparison.OrdinalIgnoreCase));

    /// <summary>One cell: the text Excel shows, and whether it holds a number (for telling headers from data).</summary>
    record struct XCell(string Text, bool Number);

    async Task<string> ExcelAsync()
    {
        const string main = "xl/workbook.xml";
        var wb = Load(main) ?? throw new InvalidDataException("This Excel file has no workbook inside.");
        var rels = Rels(main);
        var shared = new List<string>();
        var ss = Load("xl/sharedStrings.xml");
        if (ss?.Root != null)
            foreach (var item in ss.Root.Elements(S + "si"))
                shared.Add(string.Concat(item.Descendants(S + "t").Where(t => t.Parent?.Name != S + "rPh").Select(t => t.Value)));
        bool date1904 = IsTrue(wb.Root?.Element(S + "workbookPr")?.Attribute("date1904"));

        // number formats: style index -> format code
        var formats = new Dictionary<int, string>();
        var xfFormat = new List<int>();
        var styles = Load("xl/styles.xml");
        if (styles?.Root != null)
        {
            foreach (var f in styles.Root.Element(S + "numFmts")?.Elements(S + "numFmt") ?? Enumerable.Empty<XElement>())
                if (int.TryParse((string)f.Attribute("numFmtId"), out int fid)) formats[fid] = (string)f.Attribute("formatCode");
            foreach (var xf in styles.Root.Element(S + "cellXfs")?.Elements(S + "xf") ?? Enumerable.Empty<XElement>())
                xfFormat.Add(int.TryParse((string)xf.Attribute("numFmtId"), out int fid) ? fid : 0);
        }
        string FormatOf(int style)
        {
            int fid = style >= 0 && style < xfFormat.Count ? xfFormat[style] : 0;
            return formats.TryGetValue(fid, out var code) ? code : ExcelFormat.BuiltIn.TryGetValue(fid, out var b) ? b : "General";
        }

        var sheets = wb.Root?.Element(S + "sheets")?.Elements(S + "sheet").ToList() ?? new List<XElement>();
        var md = new StringBuilder();
        int hiddenSheets = 0;
        for (int k = 0; k < sheets.Count; k++)
        {
            ct.ThrowIfCancellationRequested();
            progress.Report(new Step($"Sheet {k + 1} of {sheets.Count}", (double)k / sheets.Count));
            string name = (string)sheets[k].Attribute("name") ?? $"Sheet {k + 1}";
            string state = (string)sheets[k].Attribute("state");
            if (state is "hidden" or "veryHidden") { hiddenSheets++; continue; }
            string id = (string)sheets[k].Attribute(R + "id");
            if (id == null || !rels.TryGetValue(id, out var part)) continue;
            var doc = Load(part);
            var root = doc?.Root;
            var data = root?.Element(S + "sheetData");
            if (data == null) continue;                                  // chart sheets
            var sheetRels = Rels(part);

            var hiddenCols = new HashSet<int>();
            foreach (var col in root.Element(S + "cols")?.Elements(S + "col") ?? Enumerable.Empty<XElement>())
                if (IsTrue(col.Attribute("hidden")) && int.TryParse((string)col.Attribute("min"), out int c0) && int.TryParse((string)col.Attribute("max"), out int c1))
                    for (int ci = c0; ci <= Math.Min(c1, 16384); ci++) hiddenCols.Add(ci);
            var hiddenRows = new HashSet<int>();

            // links: cell -> web address
            var links = new Dictionary<(int, int), string>();
            foreach (var h in root.Element(S + "hyperlinks")?.Elements(S + "hyperlink") ?? Enumerable.Empty<XElement>())
            {
                string rid = (string)h.Attribute(R + "id"), cref = ((string)h.Attribute("ref") ?? "").Split(':')[0];
                if (rid != null && sheetRels.TryGetValue(rid, out var url) && url.Contains(':') && cref.Length > 0)
                    links[(RowIndex(cref), ColumnIndex(cref))] = url;
            }

            var grid = new Dictionary<(int r, int c), XCell>();
            int lastRow = 0;
            foreach (var row in data.Elements(S + "row"))
            {
                int r = (int?)row.Attribute("r") ?? lastRow + 1;
                lastRow = r;
                if (IsTrue(row.Attribute("hidden"))) { hiddenRows.Add(r); continue; }
                int nextCol = 1;
                foreach (var c in row.Elements(S + "c"))
                {
                    string cref = (string)c.Attribute("r");
                    int col = cref != null ? ColumnIndex(cref) : nextCol;
                    nextCol = col + 1;
                    if (hiddenCols.Contains(col)) continue;
                    string t = (string)c.Attribute("t"), v = (string)c.Element(S + "v");
                    bool number = false;
                    string val;
                    switch (t)
                    {
                        case "s": val = int.TryParse(v, out int idx) && idx >= 0 && idx < shared.Count ? shared[idx] : ""; break;
                        case "inlineStr": val = string.Concat(c.Element(S + "is")?.Descendants(S + "t").Select(x => x.Value) ?? Enumerable.Empty<string>()); break;
                        case "b": val = v == "1" ? "TRUE" : "FALSE"; break;
                        case "e": val = v ?? ""; number = true; break;
                        case "str": val = v ?? ""; break;
                        default:
                            if (v == null) { val = ""; break; }
                            if (double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double num))
                            {
                                val = ExcelFormat.Format(num, FormatOf((int?)c.Attribute("s") ?? 0), date1904);
                                number = true;
                            }
                            else val = v;
                            break;
                    }
                    if (val.Trim().Length == 0) continue;
                    string text = Cell(val);
                    if (links.TryGetValue((r, col), out var link)) text = $"[{text}]({link})";
                    grid[(r, col)] = new XCell(text, number);
                }
            }

            md.Append($"\n\n## {name}\n\n");
            if (grid.Count == 0) md.Append("(empty sheet)\n");
            else
            {
                bool first = true;
                foreach (var block in SheetBlocks(grid))
                {
                    md.Append(RenderBlock(grid, block, first)).Append("\n\n");
                    first = false;
                }
            }

            // cell notes (comments)
            foreach (var target in sheetRels.Values.Where(t => Path.GetFileName(t).StartsWith("comments", StringComparison.OrdinalIgnoreCase)))
            {
                var cdoc = Load(target);
                var notes = new List<string>();
                foreach (var cm in cdoc?.Root?.Descendants(S + "comment") ?? Enumerable.Empty<XElement>())
                {
                    string cref = (string)cm.Attribute("ref") ?? "";
                    if (cref.Length == 0 || hiddenRows.Contains(RowIndex(cref)) || hiddenCols.Contains(ColumnIndex(cref))) continue;
                    string note = Regex.Replace(string.Concat(cm.Element(S + "text")?.Descendants(S + "t").Select(x => x.Value) ?? Enumerable.Empty<string>()), @"\s+", " ").Trim();
                    if (note.Length == 0) continue;
                    string on = grid.TryGetValue((RowIndex(cref), ColumnIndex(cref)), out var cell) ? $" ({cell.Text})" : "";
                    notes.Add($"- **{cref}**{on}: {note}");
                }
                if (notes.Count > 0) md.Append("**Notes**\n\n").Append(string.Join("\n", notes)).Append("\n\n");
            }

            // charts and pictures, top to bottom
            foreach (var dpart in sheetRels.Values.Where(t => Path.GetFileName(t).StartsWith("drawing", StringComparison.OrdinalIgnoreCase) && t.EndsWith(".xml")))
                md.Append(await DrawingAsync(dpart, name));
        }
        if (hiddenSheets > 0) Log.Write($"Excel: {hiddenSheets} hidden sheet(s) skipped");
        return md.ToString();
    }

    /// <summary>
    /// The separate tables on a sheet: groups of touching cells. A group is cut again where a new header row starts
    /// right under a table (all text, a different set of columns, after a row with numbers).
    /// </summary>
    static List<List<(int r, int c)>> SheetBlocks(Dictionary<(int r, int c), XCell> grid)
    {
        var comp = new Dictionary<(int r, int c), int>();
        var groups = new List<List<(int r, int c)>>();
        foreach (var start in grid.Keys.OrderBy(k => k.r).ThenBy(k => k.c))
        {
            if (comp.ContainsKey(start)) continue;
            var members = new List<(int r, int c)>();
            var stack = new Stack<(int r, int c)>();
            stack.Push(start);
            comp[start] = groups.Count;
            while (stack.Count > 0)
            {
                var (r, c) = stack.Pop();
                members.Add((r, c));
                for (int dr = -1; dr <= 1; dr++)
                    for (int dc = -1; dc <= 1; dc++)
                    {
                        var nb = (r + dr, c + dc);
                        if (grid.ContainsKey(nb) && !comp.ContainsKey(nb)) { comp[nb] = groups.Count; stack.Push(nb); }
                    }
            }
            groups.Add(members);
        }

        var blocks = new List<List<(int r, int c)>>();
        foreach (var g in groups)
        {
            var rows = g.Select(x => x.r).Distinct().OrderBy(x => x).ToList();
            (int, int) Span(int row) { var cs = g.Where(x => x.r == row).Select(x => x.c).ToList(); return (cs.Min(), cs.Max()); }
            var current = new List<int> { rows[0] };
            var cuts = new List<List<int>> { current };
            for (int i = 1; i < rows.Count; i++)
            {
                int a = rows[i - 1], b = rows[i];
                var rowA = g.Where(x => x.r == a).ToList();
                var rowB = g.Where(x => x.r == b).ToList();
                bool newHeader = b == a + 1 && !Span(a).Equals(Span(b)) && rowB.Count >= 2
                                 && rowB.All(x => !grid[x].Number) && rowA.Any(x => grid[x].Number);
                if (newHeader) { current = new List<int>(); cuts.Add(current); }
                current.Add(b);
            }
            foreach (var cut in cuts) blocks.Add(g.Where(x => cut.Contains(x.r)).ToList());
        }
        return blocks.OrderBy(b => b.Min(x => x.r)).ThenBy(b => b.Min(x => x.c)).ToList();
    }

    /// <summary>
    /// A block of cells: title and note lines above a table become text, a two-column list of labels and values
    /// becomes "label: value" lines, everything else a Markdown table.
    /// </summary>
    static string RenderBlock(Dictionary<(int r, int c), XCell> grid, List<(int r, int c)> block, bool firstOnSheet)
    {
        var rows = block.Select(x => x.r).Distinct().OrderBy(x => x).ToList();
        int c0 = block.Min(x => x.c), c1 = block.Max(x => x.c);
        var sb = new StringBuilder();
        bool heading = firstOnSheet;
        // single-cell rows above the table: the title, then notes
        while (rows.Count > 0 && block.Count(x => x.r == rows[0]) == 1 && (rows.Count > 1 || c0 == c1))
        {
            var only = block.First(x => x.r == rows[0]);
            if (rows.Count == 1 && !heading) break;
            sb.Append(heading ? "### " : "").Append(grid[only].Text).Append("\n\n");
            heading = false;
            rows.RemoveAt(0);
            if (c0 == c1 && rows.Count > 0 && block.Where(x => rows.Contains(x.r)).All(x => x.c == c0)) continue;
            c0 = block.Where(x => rows.Contains(x.r)).Select(x => x.c).DefaultIfEmpty(c0).Min();
            c1 = block.Where(x => rows.Contains(x.r)).Select(x => x.c).DefaultIfEmpty(c1).Max();
        }
        if (rows.Count == 0) return sb.ToString().TrimEnd();
        string At(int r, int c) => grid.TryGetValue((r, c), out var x) ? x.Text : "";

        // one column: lines of text
        if (c0 == c1)
        {
            sb.Append(string.Join("\n", rows.Select(r => At(r, c0))));
            return sb.ToString().TrimEnd();
        }
        // a label and a value per row (no header): "label: value"
        // (a header is short words; a number or a sentence on the right means the first row is already data)
        bool firstRowIsData = rows.Count == 1 || (grid.TryGetValue((rows[0], c1), out var fv)
            && (fv.Number || fv.Text.Length > 30 || fv.Text.TrimEnd().EndsWith('.')));
        if (c1 == c0 + 1 && firstRowIsData)
        {
            foreach (int r in rows)
            {
                string label = At(r, c0), value = At(r, c1);
                sb.Append("- ").Append(label.Length > 0 && value.Length > 0 ? $"**{label}:** {value}" : label + value).Append('\n');
            }
            return sb.ToString().TrimEnd();
        }
        // one row, several cells
        if (rows.Count == 1)
        {
            sb.Append(string.Join(" · ", Enumerable.Range(c0, c1 - c0 + 1).Select(c => At(rows[0], c)).Where(t => t.Length > 0)));
            return sb.ToString().TrimEnd();
        }
        var table = rows.Select(r => Enumerable.Range(c0, c1 - c0 + 1).Select(c => At(r, c)).ToList()).ToList();
        sb.Append(MarkdownTable(table));
        return sb.ToString().TrimEnd();
    }

    /// <summary>The charts and pictures placed on a sheet, in the order they appear (top to bottom, left to right).</summary>
    async Task<string> DrawingAsync(string part, string sheetName)
    {
        var doc = Load(part);
        if (doc?.Root == null) return "";
        var drels = Rels(part);
        var items = new List<(int row, int col, XElement el)>();
        foreach (var anchor in doc.Root.Elements())
        {
            var from = anchor.Element(Xdr + "from");
            int row = (int?)from?.Element(Xdr + "row") ?? 0, col = (int?)from?.Element(Xdr + "col") ?? 0;
            items.Add((row, col, anchor));
        }
        var sb = new StringBuilder();
        foreach (var (_, _, anchor) in items.OrderBy(i => i.row).ThenBy(i => i.col))
        {
            ct.ThrowIfCancellationRequested();
            foreach (var chartRef in anchor.Descendants(C + "chart"))
            {
                string rid = (string)chartRef.Attribute(R + "id");
                if (rid != null && drels.TryGetValue(rid, out var chartPart)) sb.Append(ChartText(chartPart, sheetName)).Append("\n\n");
            }
            foreach (var blip in anchor.Descendants(A + "blip"))
            {
                string rid = (string)blip.Attribute(R + "embed") ?? (string)blip.Attribute(R + "link");
                if (rid == null || !drels.TryGetValue(rid, out var target)) continue;
                string pic = await PictureAsync(target);
                if (pic != null) sb.Append(pic).Append("\n\n");
            }
        }
        return sb.ToString();
    }

    static readonly Dictionary<string, string> ChartKinds = new()
    {
        ["barChart"] = "bar", ["bar3DChart"] = "bar", ["lineChart"] = "line", ["line3DChart"] = "line", ["pieChart"] = "pie",
        ["pie3DChart"] = "pie", ["doughnutChart"] = "doughnut", ["areaChart"] = "area", ["area3DChart"] = "area",
        ["scatterChart"] = "scatter", ["radarChart"] = "radar", ["bubbleChart"] = "bubble", ["stockChart"] = "stock", ["surfaceChart"] = "surface"
    };

    /// <summary>
    /// A chart: its title, kind and data ranges, and its numbers when they come from another sheet.
    /// <paramref name="sheetName"/> null = a chart in Word or PowerPoint: always the numbers, no cell ranges
    /// (they point into a hidden workbook inside the file, which means nothing to the reader).
    /// </summary>
    string ChartText(string part, string sheetName)
    {
        var doc = Load(part);
        var chart = doc?.Root?.Element(C + "chart");
        if (chart == null) return "*(chart)*";
        string title = Regex.Replace(string.Join(" ", chart.Element(C + "title")?.Descendants(A + "t").Select(t => t.Value) ?? Enumerable.Empty<string>()), @"\s+", " ").Trim();
        var plot = chart.Element(C + "plotArea");
        var kinds = plot?.Elements().Where(e => ChartKinds.ContainsKey(e.Name.LocalName)).ToList() ?? new List<XElement>();
        string kind = string.Join(" and ", kinds.Select(e =>
            e.Name.LocalName.StartsWith("bar") && (string)e.Element(C + "barDir")?.Attribute("val") == "col" ? "column" : ChartKinds[e.Name.LocalName]).Distinct());

        var series = new List<(string name, List<string> cats, List<string> vals)>();
        var ranges = new List<string>();
        foreach (var ser in kinds.SelectMany(e => e.Elements(C + "ser")))
        {
            string sname = ser.Element(C + "tx")?.Descendants(C + "v").FirstOrDefault()?.Value ?? "";
            var cat = ser.Element(C + "cat") ?? ser.Element(C + "xVal");
            var val = ser.Element(C + "val") ?? ser.Element(C + "yVal");
            foreach (var f in new[] { cat, val }.Where(x => x != null).SelectMany(x => x.Descendants(C + "f")))
                ranges.Add(f.Value.Trim());
            series.Add((sname, Points(cat), Points(val)));
        }
        if (title.Length == 0 && series.Count == 1) title = series[0].name;
        var sb = new StringBuilder($"**Chart{(title.Length > 0 ? ": " + title : "")}**");
        if (kind.Length > 0) sb.Append($" ({kind} chart)");
        ranges = ranges.Distinct().ToList();
        if (ranges.Count > 0 && sheetName != null) sb.Append("\n\nData: ").Append(string.Join(", ", ranges));

        // the numbers themselves, unless they are in a table on this same sheet (already written above)
        bool sameSheet = false;
        if (sheetName != null)
        {
            string own = "'" + sheetName.Replace("'", "''") + "'!";
            sameSheet = ranges.Count > 0 && ranges.All(r => r.StartsWith(sheetName + "!") || r.StartsWith(own) || !r.Contains('!'));
        }
        if (!sameSheet && series.Count > 0)
        {
            int n = series.Max(s => Math.Max(s.cats.Count, s.vals.Count));
            if (n > 0 && n <= 200)
            {
                var rows = new List<List<string>> { new[] { "" }.Concat(series.Select(s => Cell(s.name))).ToList() };
                for (int i = 0; i < n; i++)
                {
                    string label = series.Select(s => i < s.cats.Count ? s.cats[i] : "").FirstOrDefault(x => x.Length > 0) ?? (i + 1).ToString();
                    rows.Add(new[] { Cell(label) }.Concat(series.Select(s => i < s.vals.Count ? Cell(s.vals[i]) : "")).ToList());
                }
                sb.Append("\n\n").Append(MarkdownTable(rows));
            }
        }
        return sb.ToString();
    }

    /// <summary>The values a chart keeps a copy of (its cache), in order, formatted like the cells.</summary>
    static List<string> Points(XElement el)
    {
        var outp = new List<string>();
        var cache = el?.Descendants().FirstOrDefault(d => d.Name == C + "strCache" || d.Name == C + "numCache" || d.Name == C + "strLit" || d.Name == C + "numLit");
        if (cache == null) return outp;
        string code = (string)cache.Element(C + "formatCode") ?? "General";
        int count = (int?)cache.Element(C + "ptCount")?.Attribute("val") ?? 0;
        var pts = new Dictionary<int, string>();
        foreach (var pt in cache.Elements(C + "pt"))
        {
            int idx = (int?)pt.Attribute("idx") ?? pts.Count;
            string v = (string)pt.Element(C + "v") ?? "";
            if (cache.Name.LocalName.StartsWith("num") && double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double d))
                v = ExcelFormat.Format(d, (string)pt.Attribute("formatCode") ?? code, false);
            pts[idx] = v;
        }
        count = Math.Max(count, pts.Count == 0 ? 0 : pts.Keys.Max() + 1);
        for (int i = 0; i < Math.Min(count, 1000); i++) outp.Add(pts.TryGetValue(i, out var s) ? s : "");
        return outp;
    }

    static int RowIndex(string cellRef)
    {
        var m = Regex.Match(cellRef, @"\d+");
        return m.Success && int.TryParse(m.Value, out int r) ? r : 0;
    }

    static int ColumnIndex(string cellRef)
    {
        int n = 0;
        foreach (char ch in cellRef)
        {
            char u = char.ToUpperInvariant(ch);
            if (u < 'A' || u > 'Z') break;
            n = n * 26 + (u - 'A' + 1);
        }
        return n;
    }
}
