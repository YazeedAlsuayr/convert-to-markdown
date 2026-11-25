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

    OfficeConverter(string path, IProgress<Step> progress, CancellationToken ct)
    {
        this.progress = progress;
        this.ct = ct;
        try { zip = ZipFile.OpenRead(path); }
        catch (InvalidDataException) { throw new InvalidDataException("This file can't be opened. It may be password-protected or damaged."); }
    }

    public void Dispose() => zip.Dispose();

    public static async Task<string> ConvertAsync(string path, IProgress<Step> progress, CancellationToken ct)
    {
        using var c = new OfficeConverter(path, progress, ct);
        string ext = Path.GetExtension(path).ToLowerInvariant();
        progress.Report(new Step("Opening the file", 0));
        string body = ext switch
        {
            ".docx" or ".docm" => await c.WordAsync(),
            ".pptx" or ".pptm" => await c.PowerPointAsync(),
            _ => c.Excel()
        };
        var md = new StringBuilder();
        md.Append(Converter.Mark).Append("\n\n# ").Append(Path.GetFileNameWithoutExtension(path)).Append("\n\n");
        md.Append(c.aiUsed
            ? $"> Text comes from the file itself (exact). Pictures are {(Settings.Describe ? "described and " : "")}read by the built-in AI ({Ai.ModelName}) and CAN contain errors.\n\n"
            : c.picturesRead > 0
            ? "> Text comes from the file itself (exact). Text inside pictures is read by Windows OCR and CAN contain errors.\n\n"
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
        return XDocument.Load(s);
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
            return text.Length == 0 ? "*(picture)*" : "**Picture text (Windows OCR - verify):**\n\n" + text;
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
                if (id != null && name != null) styleNames[id] = name.ToLowerInvariant();
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
                sb.Append(new string('#', Math.Min(heading + 1, 6))).Append(' ').Append(text);
                kind = "heading";
            }
            else
            {
                var numPr = ppr?.Element(W + "numPr");
                if (numPr != null || style.StartsWith("list"))
                {
                    int ilvl = (int?)numPr?.Element(W + "ilvl")?.Attribute(W + "val") ?? 0;
                    string numId = (string)numPr?.Element(W + "numId")?.Attribute(W + "val");
                    bool bullet = true;
                    if (numId != null && numToAbstract.TryGetValue(numId, out var abs) && abs != null
                        && listFormats.TryGetValue((abs, ilvl), out var fmt))
                        bullet = fmt is "bullet" or "none";
                    sb.Append(new string(' ', 2 * ilvl)).Append(bullet ? "- " : "1. ").Append(text);
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
        for (int i = 0; i < slides.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            progress.Report(new Step($"Slide {i + 1} of {slides.Count}", (double)i / Math.Max(1, slides.Count)));
            md.Append(await SlideAsync(slides[i], i + 1));
        }
        return md.ToString();
    }

    async Task<string> SlideAsync(string part, int number)
    {
        var doc = Load(part);
        if (doc?.Root == null) return "";
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
                else if (e.Descendants().Any(d => d.Name.LocalName == "chart"))
                    items.Add((y, x, "*(chart)*"));
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
    string Excel()
    {
        const string main = "xl/workbook.xml";
        var wb = Load(main) ?? throw new InvalidDataException("This Excel file has no workbook inside.");
        var rels = Rels(main);
        var shared = new List<string>();
        var ss = Load("xl/sharedStrings.xml");
        if (ss?.Root != null)
            foreach (var item in ss.Root.Elements(S + "si"))
                shared.Add(string.Concat(item.Descendants(S + "t").Where(t => t.Parent?.Name != S + "rPh").Select(t => t.Value)));

        var sheets = wb.Root?.Element(S + "sheets")?.Elements(S + "sheet").ToList() ?? new List<XElement>();
        var md = new StringBuilder();
        for (int k = 0; k < sheets.Count; k++)
        {
            ct.ThrowIfCancellationRequested();
            progress.Report(new Step($"Sheet {k + 1} of {sheets.Count}", (double)k / sheets.Count));
            string name = (string)sheets[k].Attribute("name") ?? $"Sheet {k + 1}";
            string id = (string)sheets[k].Attribute(R + "id");
            if (id == null || !rels.TryGetValue(id, out var part)) continue;
            var doc = Load(part);
            var data = doc?.Root?.Element(S + "sheetData");
            if (data == null) continue;                                  // chart sheets

            var grid = new SortedDictionary<int, SortedDictionary<int, string>>();
            int lastRow = 0;
            foreach (var row in data.Elements(S + "row"))
            {
                int r = (int?)row.Attribute("r") ?? lastRow + 1;
                lastRow = r;
                int nextCol = 1;
                foreach (var c in row.Elements(S + "c"))
                {
                    string cref = (string)c.Attribute("r");
                    int col = cref != null ? ColumnIndex(cref) : nextCol;
                    nextCol = col + 1;
                    string t = (string)c.Attribute("t"), v = (string)c.Element(S + "v");
                    string val = t switch
                    {
                        "s" => int.TryParse(v, out int idx) && idx >= 0 && idx < shared.Count ? shared[idx] : "",
                        "inlineStr" => string.Concat(c.Element(S + "is")?.Descendants(S + "t").Select(x => x.Value) ?? Enumerable.Empty<string>()),
                        "b" => v == "1" ? "TRUE" : "FALSE",
                        _ => v ?? ""
                    };
                    if (val.Trim().Length == 0) continue;
                    if (!grid.TryGetValue(r, out var cells)) grid[r] = cells = new SortedDictionary<int, string>();
                    cells[col] = Cell(val);
                }
            }

            md.Append($"\n\n## {name}\n\n");
            if (grid.Count == 0) { md.Append("(empty sheet)\n"); continue; }
            int minC = grid.Values.Min(r => r.Keys.Min()), maxC = Math.Min(grid.Values.Max(r => r.Keys.Max()), minC + 200);
            var rows = grid.Values.Select(cells => Enumerable.Range(minC, maxC - minC + 1)
                .Select(ci => cells.TryGetValue(ci, out var s) ? s : "").ToList()).ToList();
            md.Append(MarkdownTable(rows)).Append('\n');
        }
        return md.ToString();
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
