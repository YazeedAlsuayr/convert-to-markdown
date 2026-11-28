// PDF (and picture) -> Markdown, using only what is built into Windows:
//   typed text, highlights   - read straight from the PDF (exact)
//   handwriting              - the built-in AI reads a clean picture of the pen strokes stored in the PDF
//                              (Windows handwriting recognition when the AI is off)
//   text inside pictures     - Windows OCR (English and Arabic)

using System.IO.Compression;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Tokens;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.UI.Input.Inking;
using PigDocument = UglyToad.PdfPig.PdfDocument;
using WinPdfDocument = Windows.Data.Pdf.PdfDocument;
using WinPdfPage = Windows.Data.Pdf.PdfPage;

namespace ConvertToMarkdown;

readonly record struct Step(string Detail, double Frac);

/// <summary>A problem with the file itself, with a message written for the user (shown as it is).</summary>
sealed class FriendlyException : Exception
{
    public FriendlyException(string message, Exception inner = null) : base(message, inner) { }
}

class Note
{
    public R Box;
    public List<List<Vector2>> Strokes = new();
}

static class Converter
{
    public const string Mark = "<!-- converted by MarkdownTool -->";

    // same rules as the Python tool
    const double GapX = 28, RowOverlap = 0.35;            // pen strokes this close belong to one note
    const double HighlighterOpacity = 0.9, HighlighterWidth = 8;
    const double PictureDpi = 300;

    static readonly string[] ImageTypes = { ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff", ".gif" };
    static readonly Regex Letters = new(@"[0-9A-Za-z؀-ۿ]");
    static readonly Regex ArrowLike = new(@"^[-–—~=]+[>s7)\]}]$|^[-–—~=]{2,}$|^→$");
    static readonly Regex WordLike = new(@"^[(""'\[]?([A-Za-z][a-z]+|[A-Z]{1,}|[A-Za-z]\.|\d+([.,:/-]\d+)*%?|[ء-ي٠-٩]{2,})[.,:;!?)""'\]]*$");

    public static async Task<string> ConvertAsync(string path, IProgress<Step> progress, CancellationToken ct)
    {
        if (!File.Exists(path)) throw new FriendlyException("File not found. It may have been moved or deleted.");
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".doc" or ".ppt" or ".xls")
            throw new FriendlyException(ext switch
            {
                ".ppt" => "Old PowerPoint format. Save it as .pptx first.",
                ".xls" => "Old Excel format. Save it as .xlsx first.",
                _ => "Old Word format. Save it as .docx first."
            });
        if (ext != ".pdf" && !ImageTypes.Contains(ext) && !OfficeConverter.Types.Contains(ext))
            throw new FriendlyException("This file type isn't supported.");
        if (new FileInfo(path).Length == 0)
            throw new FriendlyException("This file is empty.");
        // can the file be read at all? (open in another program that blocks it, or no permission)
        try { using var probe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new FriendlyException(Explain(e), e); }

        string body;
        if (ext == ".pdf") body = await PdfAsync(path, progress, ct);
        else if (ImageTypes.Contains(ext)) body = await ImageAsync(path, progress, ct);
        else body = await OfficeConverter.ConvertAsync(path, progress, ct);

        string output = TargetPath(path);
        try
        {
            string tmp = Path.Combine(Program.CacheDir, $"{Environment.TickCount64}_{Path.GetFileName(output)}");
            await File.WriteAllTextAsync(tmp, body, new UTF8Encoding(false), CancellationToken.None);
            ct.ThrowIfCancellationRequested();
            if (File.Exists(output)) File.Delete(output);     // only ever our own earlier output (see TargetPath)
            File.Move(tmp, output);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new FriendlyException(IsDiskFull(e)
                ? "Disk full. Free some space and try again."
                : "Can't save here. Copy the file to Documents and try again.", e);
        }
        return output;
    }

    /// <summary>
    /// Starts the AI in the background as soon as a file has opened fine and needs it, while the text is read
    /// (saves a few seconds). Not before: a damaged or locked file shouldn't start the AI for nothing.
    /// </summary>
    internal static void WarmUpAi(CancellationToken ct)
    {
        if (!Ai.Enabled) return;
        _ = Task.Run(async () =>
        {
            try { await Ai.EnsureRunningAsync(null, ct); }
            catch (Exception e) when (e is not OutOfMemoryException) { }   // the real call later reports any problem
        });
    }

    static bool IsDiskFull(Exception e) => (e.HResult & 0xFFFF) is 112 or 39;     // ERROR_DISK_FULL, ERROR_HANDLE_DISK_FULL

    /// <summary>A short, plain reason for the progress window (the technical details go to the log).</summary>
    public static string Explain(Exception e)
    {
        int code = e.HResult & 0xFFFF;
        return e switch
        {
            FriendlyException or NotSupportedException => e.Message,
            UnauthorizedAccessException => "No permission to open this file.",
            IOException when code is 32 or 33 => "File is open in another program. Close it and try again.",
            IOException when IsDiskFull(e) => "Disk full. Free some space and try again.",
            OutOfMemoryException or InsufficientExecutionStackException => "Too big for this PC's memory.",
            _ => "Something went wrong. Details are in the log."
        };
    }

    /// <summary>name.md next to the file; never overwrites a .md this tool did not write.</summary>
    static string TargetPath(string path)
    {
        string baseName = Path.Combine(Path.GetDirectoryName(path), Path.GetFileNameWithoutExtension(path));
        string output = baseName + ".md";
        if (File.Exists(output))
        {
            string first = "";
            try { first = File.ReadLines(output).FirstOrDefault() ?? ""; } catch { }
            if (!first.Contains(Mark))
                output = baseName + "_" + Path.GetExtension(path).TrimStart('.').ToLowerInvariant() + ".md";
        }
        return output;
    }

    // ======================= PDF =======================
    static async Task<string> PdfAsync(string path, IProgress<Step> progress, CancellationToken ct)
    {
        progress.Report(new Step("Opening the PDF", 0));
        lock (askedBefore) askedBefore.Clear();          // remembered answers are for one file only
        var clock = System.Diagnostics.Stopwatch.StartNew();
        WinPdfDocument winDoc;
        PigDocument pig;
        try
        {
            var storage = await StorageFile.GetFileFromPathAsync(path);
            winDoc = await WinPdfDocument.LoadFromFileAsync(storage);
            pig = PigDocument.Open(path);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.Write("PDF open: " + e);
            bool password = (uint)e.HResult == 0x80048040                       // Windows: "password required"
                            || e.GetType().Name.Contains("Encrypt") || e.Message.Contains("encrypt", StringComparison.OrdinalIgnoreCase);
            if (password)
                throw new FriendlyException("Password-protected PDF. Remove the password and try again.", e);
            if (e is IOException or UnauthorizedAccessException) throw new FriendlyException(Explain(e), e);
            throw new FriendlyException("This PDF is damaged and can't be read.", e);
        }
        using var doc = pig;
        int n = doc.NumberOfPages;
        if (n == 0) throw new FriendlyException("This PDF has no pages.");
        WarmUpAi(ct);

        // first pass: the typed text of every page (also gives the document's own words)
        var allLines = new List<TextLine>[n + 1];
        var heights = new double[n + 1];
        // how much work each page is, for an honest "time left": text is quick, every picture and handwriting
        // means questions to the AI (a page with 3 pictures takes far longer than a page of text)
        var work = new double[n + 1];
        bool aiWork = Ai.Enabled;
        for (int i = 1; i <= n; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var pg = doc.GetPage(i); heights[i] = pg.Height; allLines[i] = TextLayer.Build(pg);
                int pictures = PictureRects(pg, skipBanners: true).Count;
                int ink = 0;
                try { ink = pg.ExperimentalAccess.GetAnnotations().Count(a => a.Type == UglyToad.PdfPig.Annotations.AnnotationType.Ink); } catch { }
                work[i] = 0.3 + (aiWork ? 5 * Math.Max(pictures, allLines[i].Count == 0 && pictures == 0 ? 0 : pictures)
                                        + (ink > 0 ? (Settings.HandwritingAi ? 3 + 0.4 * Math.Min(ink, 60) : 0.5) : 0)
                                        + (Settings.Describe ? 4 : 0)
                                        + (allLines[i].Count == 0 && pg.NumberOfImages > 0 ? 6 : 0)   // a scanned page
                                      : 0.3 * pictures + 0.2 * Math.Min(ink, 60));
            }
            catch (Exception e) { allLines[i] ??= new List<TextLine>(); work[i] = 1; Log.Write($"page {i} text: {e.Message}"); }
        }
        var workBefore = new double[n + 2];
        for (int i = 1; i <= n; i++) workBefore[i + 1] = workBefore[i] + work[i];
        double totalWork = Math.Max(workBefore[n + 1], 0.001);
        // part of the whole file done: page i, "part" (0..1) of the way through that page
        double Done(int i, double part) => (workBefore[i] + Math.Clamp(part, 0, 1) * work[i]) / totalWork;
        var repeated = RepeatedEdges(allLines, heights, n, out string headerText, out string footerText);
        var vocab = new Vocabulary(allLines.Skip(1).SelectMany(ls => ls.Select(l => l.Text)));

        var md = new StringBuilder();
        md.Append(Mark).Append("\n\n# ").Append(Path.GetFileNameWithoutExtension(path)).Append("\n\n");
        bool ai = await Ai.EnsureRunningAsync(progress, ct);
        if (!ai) Log.Write("AI not used: " + Ai.Status);
        md.Append(ai
            ? "> Typed text and highlights come from the PDF itself (exact). " +
              (Settings.Describe ? "Descriptions and the text in pictures are written" : "The text in pictures is read") +
              $" by the built-in AI ({Ai.ModelName}), " +
              (Settings.HandwritingAi ? "and so is handwriting" : "handwriting by the quick built-in reader (fast mode)") +
              "; these CAN contain errors. " +
              "'possible' words are spelling guesses from the document's own words.\n"
            : "> Typed text and highlights come from the PDF itself (exact). Handwriting and picture text are read by the quick " +
              "built-in reader (the AI is off), and CAN contain errors. 'possible' words are guesses " +
              "from the document's own words.\n");
        if (ai) md.Append($"> The AI ran on the {Ai.Device}.\n");
        else md.Append($"> The AI was not used: {Ai.Status}.\n");
        if (headerText != null) md.Append($"> Header on every page: {headerText}\n");
        if (footerText != null) md.Append($"> Footer on every page: {footerText}\n");

        for (int i = 1; i <= n; i++)
        {
            ct.ThrowIfCancellationRequested();
            string here = $"Page {i} of {n}";
            progress.Report(new Step(here + "  ·  reading text", Done(i, 0)));
            var page = doc.GetPage(i);
            var lines = allLines[i];
            string typed = TextLayer.PageText(lines.Where(l => !repeated[i].Contains(l)).ToList());
            typed = AddLinks(page, lines, typed, out string linkList);
            var P = new R(0, 0, page.Width, page.Height);
            bool bigPicture = PictureRects(page, skipBanners: true).Any(r => r.Area > 0.15 * P.Area);
            int drawings = 0;
            try { drawings = page.ExperimentalAccess.Paths.Count; } catch { }
            bool picturePage = typed.Length < 60 && (bigPicture || (typed.Length == 0 && drawings >= 3));
            bool blank = typed.Length == 0 && !picturePage && page.NumberOfImages == 0 && drawings < 3;

            md.Append($"\n\n---\n\n## Page {i}\n");
            if (!picturePage && typed.Length > 0)
                md.Append("\n### Typed text (exact)\n\n").Append(typed).Append('\n');
            if (linkList.Length > 0)
                md.Append("\n### Links\n\n").Append(linkList).Append('\n');

            // --- annotations ---
            var pens = new List<Note>();
            var highlighters = new List<R>();
            var notes = new List<string>();
            try { ReadAnnotations(doc, page, lines, pens, highlighters, notes); }
            catch (Exception e) { md.Append($"\n[could not read annotations: {e.Message}]\n"); }
            if (notes.Count > 0)
                md.Append("\n### Highlights (exact)\n\n").Append(string.Join("\n", notes)).Append('\n');

            // --- description (when ticked) and the text inside pictures ---
            using (var winPage = winDoc.GetPage((uint)(i - 1)))
            {
                bool describe = Settings.Describe && Ai.Enabled;
                var description = new List<string>();
                if (describe)
                {
                    progress.Report(new Step(here + "  ·  describing the layout", Done(i, 0.05)));
                    string layout = await AiRegionAsync(winPage, page, P, Ai.DescribePrompt, 110, 200, ct);
                    if (!string.IsNullOrWhiteSpace(layout))
                        description.Add("**Layout:** " + Regex.Replace(layout, @"\s*\n\s*", " ").Trim());
                }

                if (picturePage)
                {
                    if (description.Count > 0)
                        md.Append("\n### Description (AI - verify)\n\n").Append(string.Join("\n\n", description)).Append('\n');
                    progress.Report(new Step(here + "  ·  reading the page images", Done(i, 0.1)));
                    md.Append(Ai.Enabled ? "\n### Page text (read from the page images by the AI - verify)\n\n"
                                         : "\n### Page text (read from the page images - verify)\n\n")
                      .Append(await PicturePageAsync(winPage, page, P, ct)).Append('\n');
                }
                else
                {
                    var pics = PictureRects(page, skipBanners: true).Where(r => r.Area < 0.85 * P.Area).ToList();
                    var texts = new List<string>();
                    bool anyOcr = false;
                    for (int k = 0; k < pics.Count; k++)
                    {
                        ct.ThrowIfCancellationRequested();
                        progress.Report(new Step($"{here}  ·  reading picture {k + 1} of {pics.Count}", Done(i, (0.3 + 5.0 * k) / Math.Max(work[i], 0.001))));
                        string answer = await AiRegionAsync(winPage, page, pics[k], describe ? Ai.PicturePrompt : Ai.PictureTextPrompt, 220, 900, ct);
                        string text;
                        if (answer != null)
                        {
                            var (d, t) = Ai.SplitPicture(answer, describe);
                            if (d.Length > 0) description.Add($"**Picture {k + 1}:** {d}");
                            text = t;
                        }
                        else
                        {
                            text = await OcrRegionAsync(winPage, page, pics[k]);
                            anyOcr = true;
                        }
                        if (text.Length > 0) texts.Add($"**Picture {k + 1}**\n\n{text}");
                    }
                    if (description.Count > 0)
                        md.Append("\n### Description (AI - verify)\n\n").Append(string.Join("\n\n", description)).Append('\n');
                    if (texts.Count > 0)
                        md.Append(anyOcr ? "\n### Text in pictures (quick reader - verify)\n" : "\n### Text in pictures (AI - verify)\n")
                          .Append(string.Concat(texts.Select(t => "\n" + t + "\n")));
                }
            }

            // --- handwriting ---
            if (pens.Count > 0)
            {
                var (writing, lineMarks) = SplitMarks(pens);
                var notesOnPage = Cluster(writing);
                progress.Report(new Step($"{here}  ·  reading handwriting", Done(i, 1 - 3.0 / Math.Max(work[i], 3))));
                var read = await ReadHandwritingAsync(notesOnPage, vocab, ct);
                md.Append("\n### Handwriting (pen) - verify\n");
                bool wroteNote = false;
                var marks = new List<string>();
                for (int k = 0; k < notesOnPage.Count; k++)
                {
                    ct.ThrowIfCancellationRequested();
                    var note = notesOnPage[k];
                    string text = read[k];
                    if (IsMark(text, note.Box))
                    {
                        string on = TextLayer.WordsIn(lines, note.Box);
                        marks.Add(Letters.IsMatch(on) ? $"- pen mark on: \"{on}\"" : "- pen mark (line, arrow or bracket)");
                        continue;
                    }
                    var line = new StringBuilder($"- **{text}**");
                    if (highlighters.Any(h => h.Overlap(note.Box) > 0.5 * note.Box.Area)) line.Append("  [highlighted]");
                    string near = TextLayer.WordsIn(lines, note.Box, margin: 25, limit: 90);
                    if (near.Length > 0) line.Append($"\n  - next to: \"{near}\"");
                    var sug = text.StartsWith("(") ? new List<string>() : vocab.Suggestions(text);
                    if (sug.Count > 0) line.Append("\n  - possible: " + string.Join("; ", sug));
                    md.Append(wroteNote ? "" : "\n").Append(line).Append('\n');
                    wroteNote = true;
                }
                foreach (var m in Cluster(lineMarks))
                {
                    string on = TextLayer.WordsIn(lines, m.Box.Grow(3));
                    marks.Add(Letters.IsMatch(on) ? $"- pen mark on: \"{on}\"" : "- pen mark (line, arrow or bracket)");
                }
                // marks on typed words are listed once each; the rest are only counted
                int plain = marks.Count(x => !x.StartsWith("- pen mark on"));
                marks = marks.Where(x => x.StartsWith("- pen mark on")).Distinct().ToList();
                if (plain > 0) marks.Add(plain == 1 ? "- 1 pen mark (line, arrow or bracket)" : $"- {plain} pen marks (lines, arrows or brackets)");
                if (marks.Count > 0) md.Append('\n').Append(string.Join("\n", marks)).Append('\n');
            }
            if (blank && pens.Count == 0 && notes.Count == 0) md.Append("\n*(blank page)*\n");
            Log.Write($"page {i} of {n} done");
        }
        progress.Report(new Step("Saving", 1));
        Log.Write($"{Path.GetFileName(path)}: {n} pages in {clock.Elapsed.TotalSeconds:0} s");
        return md.ToString();
    }

    // ---------------- annotations ----------------
    static IToken Resolve(PigDocument doc, IToken t)
    {
        int guard = 0;
        while (t is IndirectReferenceToken r && guard++ < 16)
            t = doc.Structure.GetObject(r.Data).Data;
        return t;
    }

    static double Num(PigDocument doc, IToken t) => Resolve(doc, t) is NumericToken n ? n.Data : double.NaN;

    static string Str(PigDocument doc, IToken t) => Resolve(doc, t) switch
    {
        StringToken s => s.Data,
        HexToken h => h.Data,
        _ => ""
    };

    static void ReadAnnotations(PigDocument doc, Page page, List<TextLine> lines,
                                List<Note> pens, List<R> highlighters, List<string> notes)
    {
        double H = page.Height;
        var shapes = new List<(string name, R box)>();
        foreach (var a in page.ExperimentalAccess.GetAnnotations())
        {
            var d = a.AnnotationDictionary.Data;
            if (!d.TryGetValue("Subtype", out var st) || Resolve(doc, st) is not NameToken nt) continue;
            string type = nt.Data;

            R rect = default;
            if (d.TryGetValue("Rect", out var rt) && Resolve(doc, rt) is ArrayToken ra && ra.Data.Count == 4)
            {
                double x0 = Num(doc, ra.Data[0]), y0 = Num(doc, ra.Data[1]), x1 = Num(doc, ra.Data[2]), y1 = Num(doc, ra.Data[3]);
                rect = new R(Math.Min(x0, x1), H - Math.Max(y0, y1), Math.Max(x0, x1), H - Math.Min(y0, y1));
            }

            if (type == "Ink")
            {
                double opacity = d.TryGetValue("CA", out var ca) ? Num(doc, ca) : 1.0;
                double width = 1;
                if (d.TryGetValue("BS", out var bs) && Resolve(doc, bs) is DictionaryToken bsd && bsd.Data.TryGetValue("W", out var w))
                    width = Num(doc, w);
                var note = ReadInk(doc, d, H);
                if (note == null) continue;
                bool highlighter = (!double.IsNaN(opacity) && opacity < HighlighterOpacity) || (!double.IsNaN(width) && width >= HighlighterWidth);
                if (highlighter) highlighters.Add(note.Box);
                else
                    foreach (var stroke in note.Strokes)            // one item per stroke; grouped again later
                        pens.Add(new Note
                        {
                            Box = new R(stroke.Min(v => v.X), stroke.Min(v => v.Y), stroke.Max(v => v.X), stroke.Max(v => v.Y)),
                            Strokes = { stroke }
                        });
            }
            else if (type is "Highlight" or "Underline" or "StrikeOut" or "Squiggly" && a.QuadPoints != null && a.QuadPoints.Count > 0)
            {
                // one quad per highlighted line: only the words inside each quad, joined in order
                var parts = new List<string>();
                foreach (var q in a.QuadPoints)
                {
                    var pts = q.Points;                                   // a span of 4 corners
                    if (pts.Length < 4) continue;
                    double qx0 = double.MaxValue, qx1 = double.MinValue, qy0 = double.MaxValue, qy1 = double.MinValue;
                    foreach (var pt in pts)
                    {
                        qx0 = Math.Min(qx0, pt.X); qx1 = Math.Max(qx1, pt.X);
                        qy0 = Math.Min(qy0, pt.Y); qy1 = Math.Max(qy1, pt.Y);
                    }
                    var box = new R(qx0, H - qy1, qx1, H - qy0);
                    double hgt = box.H;
                    string t = TextLayer.WordsIn(lines, new R(box.X0, box.Y0 + 0.2 * hgt, box.X1, box.Y1 - 0.2 * hgt), limit: 4000);
                    if (t.Length > 0 && (parts.Count == 0 || parts[^1] != t)) parts.Add(t);
                }
                if (parts.Count > 0) notes.Add($"- {type.ToLowerInvariant()}: \"{string.Join(" ", parts)}\"");
                else shapes.Add((type, rect));
            }
            else if (type is "Highlight" or "Underline" or "StrikeOut" or "Squiggly" or "Square" or "Circle" or "Line")
                shapes.Add((type, rect));
            else if (type is "FreeText" or "Text")
            {
                string c = d.TryGetValue("Contents", out var ct) ? Str(doc, ct).Trim() : "";
                if (c.Length > 0) notes.Add("- note: " + c.Replace("\r", " ").Replace("\n", " "));
            }
        }
        foreach (var (name, box) in shapes)
        {
            string t = TextLayer.WordsIn(lines, box);
            if (t.Length > 0) notes.Add($"- {name.ToLowerInvariant()}: \"{t}\"");
        }
        foreach (var box in highlighters)
        {
            string t = TextLayer.WordsIn(lines, box);
            if (t.Length > 0) notes.Add($"- highlighted: \"{t}\"");
        }
    }

    /// <summary>
    /// Lines at the very top or bottom of the page that repeat on most pages (a running title, "© 2026 …", page numbers).
    /// They are left out of each page and shown once at the start.
    /// </summary>
    static HashSet<TextLine>[] RepeatedEdges(List<TextLine>[] all, double[] heights, int n, out string header, out string footer)
    {
        header = footer = null;
        var result = new HashSet<TextLine>[n + 1];
        for (int i = 0; i <= n; i++) result[i] = new HashSet<TextLine>();
        static string Key(string t) => Regex.Replace(t.Trim().ToLowerInvariant(), @"\d+", "#");
        var pagesWith = new Dictionary<string, HashSet<int>>();
        var example = new Dictionary<string, (string text, bool top)>();
        for (int i = 1; i <= n; i++)
        {
            if (all[i] == null || heights[i] <= 0) continue;
            foreach (var l in all[i])
            {
                bool top = l.Box.Y1 < 0.09 * heights[i], bottom = l.Box.Y0 > 0.91 * heights[i];
                if (!top && !bottom) continue;
                string k = (top ? "T:" : "B:") + Key(l.Text);
                if (!pagesWith.TryGetValue(k, out var set)) pagesWith[k] = set = new HashSet<int>();
                set.Add(i);
                if (!example.ContainsKey(k)) example[k] = (l.Text.Trim(), top);
            }
        }
        var keys = pagesWith.Where(kv => kv.Value.Count >= 3 && kv.Value.Count >= 0.5 * n).Select(kv => kv.Key).ToHashSet();
        if (keys.Count == 0) return result;
        for (int i = 1; i <= n; i++)
            if (all[i] != null && heights[i] > 0)
                foreach (var l in all[i])
                {
                    bool top = l.Box.Y1 < 0.09 * heights[i], bottom = l.Box.Y0 > 0.91 * heights[i];
                    if ((top || bottom) && keys.Contains((top ? "T:" : "B:") + Key(l.Text))) result[i].Add(l);
                }
        bool PageNumber(string t) => Regex.IsMatch(t, @"^(page\s*)?#(\s*(of|/)\s*#)?$|^-\s*#\s*-$", RegexOptions.IgnoreCase);
        var shown = keys.Where(k => !PageNumber(k.Substring(2))).Select(k => example[k]).ToList();
        if (shown.Any(e => e.top)) header = string.Join(" · ", shown.Where(e => e.top).Select(e => e.text));
        if (shown.Any(e => !e.top)) footer = string.Join(" · ", shown.Where(e => !e.top).Select(e => e.text));
        return result;
    }

    /// <summary>Web links on the page: written as [text](address) in the typed text, or listed when the text is not found.</summary>
    static string AddLinks(Page page, List<TextLine> lines, string typed, out string list)
    {
        list = "";
        IReadOnlyList<Hyperlink> links;
        try { links = page.GetHyperlinks(); }
        catch { return typed; }
        if (links == null || links.Count == 0) return typed;
        double H = page.Height;
        var extra = new List<string>();
        foreach (var h in links)
        {
            if (string.IsNullOrWhiteSpace(h.Uri)) continue;
            var b = h.Bounds;
            var box = new R(Math.Min(b.Left, b.Right), H - Math.Max(b.Top, b.Bottom), Math.Max(b.Left, b.Right), H - Math.Min(b.Top, b.Bottom));
            string text = TextLayer.WordsIn(lines, box, limit: 300).Replace(" / ", " ").Trim();
            if (text.Length == 0) continue;
            string md = $"[{text}]({h.Uri.Trim()})";
            int at = typed.IndexOf(text, StringComparison.Ordinal);
            if (at >= 0 && typed.IndexOf(text, at + 1, StringComparison.Ordinal) < 0 && !typed.Contains(md))
                typed = typed.Substring(0, at) + md + typed.Substring(at + text.Length);
            else if (!typed.Contains(md)) extra.Add("- " + md);
        }
        list = string.Join("\n", extra.Distinct());
        return typed;
    }

    static Note ReadInk(PigDocument doc, IReadOnlyDictionary<string, IToken> d, double H)
    {
        if (!d.TryGetValue("InkList", out var inkTok) || Resolve(doc, inkTok) is not ArrayToken inkList) return null;
        var note = new Note();
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var pathTok in inkList.Data)
        {
            if (Resolve(doc, pathTok) is not ArrayToken p) continue;
            var stroke = new List<Vector2>();
            for (int i = 0; i + 1 < p.Data.Count; i += 2)
            {
                double x = Num(doc, p.Data[i]), y = H - Num(doc, p.Data[i + 1]);   // PDF y goes up; flip it
                if (double.IsNaN(x) || double.IsNaN(y)) continue;
                stroke.Add(new Vector2((float)x, (float)y));
                minX = Math.Min(minX, x); minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y);
            }
            if (stroke.Count > 0) note.Strokes.Add(stroke);
        }
        if (note.Strokes.Count == 0) return null;
        note.Box = new R(minX, minY, maxX, maxY);
        return note;
    }

    /// <summary>
    /// Long straight strokes (brackets, arrows, underlines) and very tall ones are marks, not writing.
    /// A straight stroke that crosses through a letter (the bar of a T, t, f, +, ≠) stays with the writing.
    /// </summary>
    static (List<Note> writing, List<Note> marks) SplitMarks(List<Note> strokes)
    {
        var heights = strokes.Select(n => n.Box.H).OrderBy(h => h).ToList();
        double q75 = Math.Max(heights.Count > 0 ? heights[(int)(heights.Count * 0.75)] : 10, 1);
        var tall = new HashSet<Note>();
        var straight = new HashSet<Note>();
        foreach (var n in strokes)
        {
            var s = n.Strokes[0];
            double length = 0;
            for (int i = 1; i < s.Count; i++) length += Vector2.Distance(s[i - 1], s[i]);
            double diag = Math.Sqrt(n.Box.W * n.Box.W + n.Box.H * n.Box.H);
            if (n.Box.H > 3.5 * q75) tall.Add(n);
            else if (n.Box.H > 3 * n.Box.W && n.Box.H < 3.2 * q75) continue;    // an upright l, I, 1 or | is a letter
            else if (diag > 0 && length < 1.3 * diag && diag > 2.2 * q75) straight.Add(n);
        }
        var letters = strokes.Where(n => !tall.Contains(n) && !straight.Contains(n)).ToList();
        var writing = new List<Note>(letters);
        var marks = new List<Note>(tall);
        foreach (var n in straight)
        {
            var s = n.Strokes[0];
            int from = s.Count / 3, to = Math.Max(from + 1, 2 * s.Count / 3);
            bool crossesLetter = false;
            for (int i = from; i < to && i < s.Count && !crossesLetter; i++)
                foreach (var l in letters)
                {
                    var b = l.Box.Grow(1.5);
                    if (s[i].X >= b.X0 && s[i].X <= b.X1 && s[i].Y >= b.Y0 && s[i].Y <= b.Y1) { crossesLetter = true; break; }
                }
            (crossesLetter ? writing : marks).Add(n);
        }
        return (writing, marks);
    }

    /// <summary>Strokes on the same line of writing, touching or close together.</summary>
    static bool SameRow(R a, R b)
    {
        double gx = Math.Max(Math.Max(b.X0 - a.X1, a.X0 - b.X1), 0);
        double ov = Math.Min(a.Y1, b.Y1) - Math.Max(a.Y0, b.Y0);
        double h = Math.Min(a.H, b.H);
        if (gx == 0 && h > 0 && ov >= 0.5 * h) return true;
        return gx <= GapX && h > 0 && ov >= RowOverlap * h;
    }

    /// <summary>The tail of a g or y, the dot of an i, an accent: a tiny piece just under or over a word.</summary>
    static bool IsTailOf(Note piece, Note word, double letterH)
    {
        R small = piece.Box, big = word.Box;
        if (piece.Strokes.Count > 2 || big.H > 3.5 * letterH || small.H > 2 * letterH || small.W > 1.5 * letterH) return false;
        double cx = (small.X0 + small.X1) / 2;
        double gy = Math.Max(small.Y0 - big.Y1, big.Y0 - small.Y1);
        return small.H < 0.7 * big.H && small.W < 0.8 * big.H && cx > big.X0 && cx < big.X1 && gy < 0.15 * big.H;
    }

    /// <summary>
    /// Pen strokes -> notes. First strokes on the same line are joined into words and lines; only then are
    /// leftover tiny pieces (dots, tails) attached to the word they belong to. Done in this order, a short note
    /// written just under another one (e.g. "UTI" under "Autoimmune") stays a separate note instead of being
    /// pulled in stroke by stroke.
    /// </summary>
    static List<Note> Cluster(List<Note> notes)
    {
        notes = notes.ToList();
        // typical height of one pen stroke = about one letter (before any strokes are joined; the upper quarter,
        // so dots and short dashes don't make it too small)
        var hs = notes.Select(n => n.Box.H).Where(x => x > 0).OrderBy(x => x).ToList();
        double letterH = hs.Count > 0 ? Math.Max(hs[(int)(hs.Count * 0.75)], 1) : 10;

        void Join(int i, int j)
        {
            notes[i].Strokes.AddRange(notes[j].Strokes);
            notes[i].Box = notes[i].Box.Union(notes[j].Box);
            notes.RemoveAt(j);
        }
        void JoinRows()
        {
            for (bool merged = true; merged;)
            {
                merged = false;
                for (int i = 0; i < notes.Count && !merged; i++)
                    for (int j = i + 1; j < notes.Count && !merged; j++)
                        if (SameRow(notes[i].Box, notes[j].Box)) { Join(i, j); merged = true; }
            }
        }

        JoinRows();
        // tiny leftover pieces join the word they hang from (a word itself is never pulled into another word)
        for (bool merged = true; merged;)
        {
            merged = false;
            for (int i = 0; i < notes.Count && !merged; i++)
                for (int j = 0; j < notes.Count && !merged; j++)
                    if (i != j && notes[j].Strokes.Count <= 2 && notes[i].Strokes.Count > notes[j].Strokes.Count
                        && IsTailOf(notes[j], notes[i], letterH))
                    {
                        notes[i].Strokes.AddRange(notes[j].Strokes);
                        notes[i].Box = notes[i].Box.Union(notes[j].Box);
                        notes.RemoveAt(j);
                        merged = true;
                    }
        }
        JoinRows();                                           // a tail can bring two parts of a line together
        return notes.OrderBy(n => Math.Round(n.Box.Y0 / 25)).ThenBy(n => n.Box.X0).ToList();
    }

    // ---------------- handwriting ----------------
    static async Task<string> RecognizeInkAsync(Note note, Vocabulary vocab)
    {
        if (!Engines.InkAvailable) return "(the quick handwriting reader is not installed in Windows)";
        try
        {
            const float scale = 3f;   // PDF points -> roughly the size of real pen writing on screen
            var container = new InkStrokeContainer();
            var builder = new InkStrokeBuilder();
            foreach (var s in note.Strokes)
            {
                if (s.Count < 2) continue;
                var pts = s.Select(p => new InkPoint(new Windows.Foundation.Point(
                    (p.X - note.Box.X0) * scale, (p.Y - note.Box.Y0) * scale), 0.5f)).ToList();
                container.AddStroke(builder.CreateStrokeFromInkPoints(pts, Matrix3x2.Identity));
            }
            if (container.GetStrokes().Count == 0) return "";

            // a fresh recognizer for every note: reusing one right after it finished makes Windows refuse
            IReadOnlyList<InkRecognitionResult> results = null;
            for (int attempt = 0; results == null; attempt++)
            {
                try
                {
                    results = await Engines.NewInk().RecognizeAsync(container, InkRecognitionTarget.All);
                }
                catch (Exception e) when (attempt < 4 && e is not OperationCanceledException)
                {
                    await Task.Delay(150 * (attempt + 1));
                }
            }

            var words = new List<string>();
            foreach (var r in results)
            {
                var cands = r.GetTextCandidates();
                if (cands.Count == 0) continue;
                string pick = cands[0];
                // the recognizer's own second guesses: prefer one that is a word from the document or the word list
                if (!vocab.Knows(pick) && !IsAbbreviation(pick))
                    pick = cands.Skip(1).FirstOrDefault(c => Vocabulary.Key(c).Length >= 4 && vocab.Knows(c)) ?? pick;
                words.Add(pick);
            }
            words = words.Select(w => ArrowLike.IsMatch(w) ? "→" : w).ToList();
            return string.Join(" ", words).Trim();
        }
        catch (Exception e)
        {
            Log.Write("handwriting: " + e);
            return "(could not read this note)";
        }
    }

    // ---------------- handwriting read by the AI ----------------
    const int NotesPerPicture = 8;

    internal static string NotesPrompt(int count) =>
        $"This picture shows {count} separate pieces of handwriting, one under the other. " +
        $"Copy each one exactly as written, in English or Arabic, one answer line per piece, top to bottom: exactly {count} lines. " +
        "Abbreviations and short notes are common. Write arrows as →. " +
        "If a piece has no letters or numbers (only a line, arrow, tick, bracket or circle), write NONE for it. " +
        "Copy the letters you actually see, even when the spelling looks wrong; never replace a word with a different word. " +
        "Never add words that are not written. Plain text only, no numbering.";

    const string NotePrompt =
        "This picture shows handwriting, in English or Arabic. Copy it exactly as written, " +
        "on one line. Abbreviations and short notes are common. Write arrows as →. " +
        "If it has no letters or numbers (only a line, arrow, tick, bracket or circle), write exactly: NONE. " +
        "Copy the letters you actually see, even when the spelling looks wrong; never replace a word with a different word. " +
        "Never add words that are not written. Plain text only.";

    /// <summary>
    /// The text of every pen note on a page. The strokes are drawn black on white and read by the built-in AI,
    /// a few notes per picture (one under the other); if the AI's answer does not have one line per note, each note
    /// is asked about on its own. Windows handwriting recognition is used when the AI is off or fails.
    /// </summary>
    static async Task<List<string>> ReadHandwritingAsync(List<Note> notes, Vocabulary vocab, CancellationToken ct)
    {
        var result = new string[notes.Count];
        bool ai = false;
        try { ai = Ai.Enabled && Settings.HandwritingAi && await Ai.EnsureRunningAsync(null, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { }
        if (ai)
        {
            try
            {
                // one or two strokes that Windows sees as a line, arrow or tick are not worth asking the AI about
                var ask = new List<int>();
                for (int k = 0; k < notes.Count; k++)
                {
                    if (notes[k].Strokes.Count <= 2 && Engines.InkAvailable && IsMark(await RecognizeInkAsync(notes[k], vocab), notes[k].Box))
                        result[k] = "";
                    else ask.Add(k);
                }
                for (int start = 0; start < ask.Count; start += NotesPerPicture)
                {
                    ct.ThrowIfCancellationRequested();
                    var idx = ask.Skip(start).Take(NotesPerPicture).ToList();
                    var chunk = idx.Select(k => notes[k]).ToList();
                    var lines = await ReadNotesAsync(chunk, ct);
                    for (int k = 0; k < chunk.Count; k++)
                        result[idx[k]] = Regex.IsMatch(lines[k], @"^\W*NONE\W*$", RegexOptions.IgnoreCase) ? "" : lines[k];
                }
                return result.ToList();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception e) { Log.Write("handwriting AI: " + e.Message + " - using Windows handwriting recognition"); }
        }
        for (int k = 0; k < notes.Count; k++)
            result[k] ??= await RecognizeInkAsync(notes[k], vocab);
        return result.ToList();
    }

    /// <summary>
    /// One answer line per note. Several notes go in one picture; when the AI's answer has the wrong number of
    /// lines, the notes are split in two halves and each half is asked again (a single note is asked on its own).
    /// That needs about 2-3 extra questions instead of one per note.
    /// </summary>
    static async Task<List<string>> ReadNotesAsync(List<Note> chunk, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (chunk.Count == 1)
            return new List<string> { await Ai.AskAsync(NotePrompt, InkPng(chunk), 60, true, ct) };
        string answer = await Ai.AskAsync(NotesPrompt(chunk.Count), InkPng(chunk), 40 + 40 * chunk.Count, false, ct);
        var lines = answer.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        for (int k = 0; k < lines.Count; k++)
            lines[k] = Regex.Replace(lines[k], $@"^{k + 1}[.)]\s+", "");
        if (lines.Count == chunk.Count) return lines;
        Log.Write($"handwriting: {lines.Count} lines for {chunk.Count} notes, asking again in two halves");
        int half = chunk.Count / 2;
        var first = await ReadNotesAsync(chunk.Take(half).ToList(), ct);
        var second = await ReadNotesAsync(chunk.Skip(half).ToList(), ct);
        return first.Concat(second).ToList();
    }

    /// <summary>Pen notes drawn black on white, one under the other, each scaled to about the same writing height.</summary>
    static byte[] InkPng(List<Note> notes)
    {
        const double lineHeight = 56, maxWidth = 1300;
        const int pad = 24, gap = 44;
        var scales = notes.Select(n =>
        {
            // a note of several lines: scale by the height of one line of writing, not the whole note
            var hs = n.Strokes.Select(st => st.Max(v => v.Y) - st.Min(v => v.Y)).OrderBy(x => x).ToList();
            double row = Math.Min(n.Box.H, 2.5 * Math.Max(hs[hs.Count / 2], 2));
            double sc = Math.Clamp(lineHeight / Math.Max(row, 4), 1.5, 6);
            return Math.Min(sc, maxWidth / Math.Max(n.Box.W, 1));
        }).ToList();
        int width = (int)Math.Ceiling(notes.Select((n, k) => n.Box.W * scales[k]).Max()) + 2 * pad;
        int height = (int)Math.Ceiling(notes.Select((n, k) => n.Box.H * scales[k]).Sum()) + gap * (notes.Count - 1) + 2 * pad;
        width = Math.Max(width, 64); height = Math.Max(height, 64);
        var px = new byte[width * height];
        Array.Fill(px, (byte)255);

        double top = pad;
        for (int k = 0; k < notes.Count; k++)
        {
            var n = notes[k];
            double sc = scales[k];
            double r = Math.Clamp(0.45 * sc, 1.6, 3.2);                  // pen radius in pixels
            foreach (var s in n.Strokes)
            {
                Vector2 P(Vector2 v) => new((float)(pad + (v.X - n.Box.X0) * sc), (float)(top + (v.Y - n.Box.Y0) * sc));
                if (s.Count == 1) { Dot(px, width, height, P(s[0]), r); continue; }
                for (int i = 1; i < s.Count; i++)
                {
                    Vector2 a = P(s[i - 1]), b = P(s[i]);
                    int steps = (int)Math.Ceiling(Vector2.Distance(a, b) / 0.7) + 1;
                    for (int t = 0; t <= steps; t++) Dot(px, width, height, Vector2.Lerp(a, b, (float)t / steps), r);
                }
            }
            top += n.Box.H * sc + gap;
        }
        return GrayPng(px, width, height);
    }

    static void Dot(byte[] px, int w, int h, Vector2 c, double r)
    {
        int x0 = Math.Max(0, (int)(c.X - r - 1)), x1 = Math.Min(w - 1, (int)(c.X + r + 1));
        int y0 = Math.Max(0, (int)(c.Y - r - 1)), y1 = Math.Min(h - 1, (int)(c.Y + r + 1));
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                double d = Math.Sqrt((x + 0.5 - c.X) * (x + 0.5 - c.X) + (y + 0.5 - c.Y) * (y + 0.5 - c.Y));
                double ink = Math.Clamp(r + 0.5 - d, 0, 1);                 // soft edge
                byte v = (byte)(255 * (1 - ink));
                if (v < px[y * w + x]) px[y * w + x] = v;
            }
    }

    /// <summary>A grayscale PNG, written directly (no drawing library needed).</summary>
    static byte[] GrayPng(byte[] px, int w, int h)
    {
        var raw = new byte[h * (w + 1)];
        for (int y = 0; y < h; y++) System.Buffer.BlockCopy(px, y * w, raw, y * (w + 1) + 1, w);   // filter byte 0 per row
        byte[] data;
        using (var z = new MemoryStream())
        {
            using (var zs = new ZLibStream(z, CompressionLevel.Fastest, leaveOpen: true)) zs.Write(raw, 0, raw.Length);
            data = z.ToArray();
        }
        using var ms = new MemoryStream();
        ms.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var ihdr = new byte[13];
        BE(ihdr, 0, (uint)w); BE(ihdr, 4, (uint)h);
        ihdr[8] = 8; ihdr[9] = 0; ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;   // 8-bit grayscale
        Chunk(ms, "IHDR", ihdr);
        Chunk(ms, "IDAT", data);
        Chunk(ms, "IEND", Array.Empty<byte>());
        return ms.ToArray();
    }

    static void BE(byte[] b, int at, uint v) { b[at] = (byte)(v >> 24); b[at + 1] = (byte)(v >> 16); b[at + 2] = (byte)(v >> 8); b[at + 3] = (byte)v; }

    static void Chunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4]; BE(len, 0, (uint)data.Length); s.Write(len);
        var body = new byte[4 + data.Length];
        Encoding.ASCII.GetBytes(type).CopyTo(body, 0);
        data.CopyTo(body, 4);
        s.Write(body);
        var crc = new byte[4]; BE(crc, 0, Crc32(body)); s.Write(crc);
    }

    static uint[] crcTable;
    static uint Crc32(byte[] data)
    {
        if (crcTable == null)
        {
            var t = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                t[n] = c;
            }
            crcTable = t;
        }
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in data) crc = crcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }

    static bool IsAbbreviation(string w) => w.Length <= 5 && w.Any(char.IsLetter) && w.Where(char.IsLetter).All(char.IsUpper);

    /// <summary>Lines, arrows, ticks and brackets: no letters, or one letter drawn as a long thin stroke.</summary>
    static bool IsMark(string text, R box)
    {
        int letters = Letters.Matches(text).Count;
        if (letters == 0) return true;
        return letters <= 1;                                  // a lone stroke read as one letter
    }

    // ---------------- pictures ----------------
    static List<R> PictureRects(Page page, bool skipBanners = false)
    {
        double H = page.Height;
        var P = new R(0, 0, page.Width, page.Height);
        var rects = new List<R>();
        try
        {
            foreach (var img in page.GetImages())
            {
                var b = img.Bounds;
                var r = new R(Math.Max(b.Left, 0), Math.Max(H - b.Top, 0), Math.Min(b.Right, P.X1), Math.Min(H - b.Bottom, P.Y1));
                bool banner = skipBanners && r.Y0 < 0.06 * P.H && r.W > 4 * r.H;     // title bar across the top
                if (r.W >= 40 && r.H >= 25 && r.Area >= 0.012 * P.Area && !banner)
                    rects.Add(r);
            }
        }
        catch (Exception e) { Log.Write("pictures: " + e.Message); }

        bool merged = true;
        while (merged)
        {
            merged = false;
            for (int i = 0; i < rects.Count && !merged; i++)
                for (int j = i + 1; j < rects.Count && !merged; j++)
                    if (rects[i].Overlap(rects[j]) > 0.3 * Math.Min(rects[i].Area, rects[j].Area))
                    {
                        rects[i] = rects[i].Union(rects[j]);
                        rects.RemoveAt(j);
                        merged = true;
                    }
        }
        double mid = P.W / 2;
        int Col(R r) => r.W > 0.6 * P.W ? 0 : ((r.X0 + r.X1) / 2 < mid ? 0 : 1);
        return rects.OrderBy(Col).ThenBy(r => r.Y0).ToList();
    }

    /// <summary>Pages made of screenshots or scans: read each picture separately, left column first.</summary>
    static async Task<string> PicturePageAsync(WinPdfPage winPage, Page page, R P, CancellationToken ct)
    {
        var rects = PictureRects(page).Where(r => r.Area < 0.9 * P.Area).ToList();
        if (rects.Count < 2)
        {
            string t = await AiRegionAsync(winPage, page, P, Ai.FullPrompt, 220, 1500, ct) ?? await OcrRegionAsync(winPage, page, P);
            return t.Length > 0 ? t : "(no readable text on this page)";
        }
        var parts = new List<string>();
        for (int k = 0; k < rects.Count; k++)
        {
            ct.ThrowIfCancellationRequested();
            string t = await AiRegionAsync(winPage, page, rects[k], Ai.FullPrompt, 220, 900, ct) ?? await OcrRegionAsync(winPage, page, rects[k]);
            parts.Add($"**Picture {k + 1}**\n\n{(t.Length > 0 ? t : "(no readable text)")}");
        }
        return string.Join("\n\n", parts);
    }

    /// <summary>Asks the built-in AI about part of a page; null when the AI is off or fails (Windows OCR is used then).</summary>
    static async Task<string> AiRegionAsync(WinPdfPage winPage, Page page, R rect, string prompt, double dpi, int maxTokens, CancellationToken ct)
    {
        if (!Ai.Enabled) return null;
        try
        {
            if (!await Ai.EnsureRunningAsync(null, ct)) return null;
            // smaller pictures = much faster for the AI; screenshot pages keep more detail for small text
            int maxSide = prompt == Ai.DescribePrompt ? 768 : prompt == Ai.PicturePrompt ? 1280 : 1600;   // layout only needs a rough look
            byte[] png = await RenderPngAsync(winPage, page, rect, dpi, maxSide);
            return await AskOnceAsync(prompt, png, maxTokens, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e)
        {
            Log.Write("AI: " + e.Message);
            return null;
        }
    }

    // answers for pictures already asked about in this file (the same logo or picture on several pages)
    static readonly Dictionary<string, string> askedBefore = new();

    /// <summary>
    /// Asks the AI about a picture, but not twice about the exact same picture, and not at all about a picture
    /// that is almost all one color (an empty box or a plain background has nothing to read).
    /// </summary>
    static async Task<string> AskOnceAsync(string prompt, byte[] png, int maxTokens, CancellationToken ct)
    {
        string key = prompt + "|" + maxTokens + "|" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(png));
        lock (askedBefore) if (askedBefore.TryGetValue(key, out var known)) { Log.Write("AI: same picture as before, answer reused"); return known; }
        string answer;
        if (await IsPlainAsync(png)) { Log.Write("AI: empty picture skipped"); answer = ""; }
        else answer = await Ai.AskAsync(prompt, png, maxTokens, false, ct);
        lock (askedBefore) askedBefore[key] = answer;
        return answer;
    }

    /// <summary>True when (almost) every pixel has the same color: nothing to read.</summary>
    static async Task<bool> IsPlainAsync(byte[] png)
    {
        try
        {
            using var ms = new MemoryStream(png);
            var dec = await BitmapDecoder.CreateAsync(ms.AsRandomAccessStream());
            var tr = new BitmapTransform { ScaledWidth = Math.Min(dec.PixelWidth, 400u), ScaledHeight = (uint)Math.Max(1, dec.PixelHeight * Math.Min(dec.PixelWidth, 400u) / Math.Max(1u, dec.PixelWidth)) };
            var data = await dec.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, tr,
                                                   ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
            byte[] px = data.DetachPixelData();
            int n = px.Length / 4;
            if (n == 0) return true;
            // the most common color (coarse), then count pixels clearly different from it
            var counts = new Dictionary<int, int>();
            for (int i = 0; i < px.Length; i += 4)
            {
                int c = (px[i] >> 4) | (px[i + 1] >> 4 << 4) | (px[i + 2] >> 4 << 8);
                counts[c] = counts.GetValueOrDefault(c) + 1;
            }
            int bg = counts.MaxBy(kv => kv.Value).Key;
            int b0 = (bg & 15) << 4, g0 = (bg >> 4 & 15) << 4, r0 = (bg >> 8 & 15) << 4;
            int different = 0;
            for (int i = 0; i < px.Length; i += 4)
                if (Math.Abs(px[i] - b0) + Math.Abs(px[i + 1] - g0) + Math.Abs(px[i + 2] - r0) > 60) different++;
            return different < n * 0.0005;       // fewer than 1 in 2000 pixels: not even one short word
        }
        catch { return false; }
    }

    static async Task<byte[]> RenderPngAsync(WinPdfPage winPage, Page page, R rect, double dpi, int maxSide)
    {
        double fx = winPage.Size.Width / page.Width, fy = winPage.Size.Height / page.Height;
        double w = rect.W * dpi / 72, h = rect.H * dpi / 72;
        double s = Math.Min(1.0, maxSide / Math.Max(w, h));
        var options = new Windows.Data.Pdf.PdfPageRenderOptions
        {
            SourceRect = new Windows.Foundation.Rect(rect.X0 * fx, rect.Y0 * fy, rect.W * fx, rect.H * fy),
            DestinationWidth = (uint)Math.Max(1, w * s),
            DestinationHeight = (uint)Math.Max(1, h * s),
            BitmapEncoderId = BitmapEncoder.PngEncoderId
        };
        using var stream = new InMemoryRandomAccessStream();
        await winPage.RenderToStreamAsync(stream, options);
        return await ReadAllAsync(stream);
    }

    static async Task<byte[]> ReadAllAsync(IRandomAccessStream stream)
    {
        var bytes = new byte[stream.Size];
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        await reader.LoadAsync((uint)stream.Size);
        reader.ReadBytes(bytes);
        return bytes;
    }

    /// <summary>A picture as PNG for the AI.</summary>
    internal static async Task<byte[]> PngAsync(SoftwareBitmap bitmap, int maxSide = 1600)
    {
        using var straight = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight);
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetSoftwareBitmap(straight);
        double s = Math.Min(1.0, (double)maxSide / Math.Max(bitmap.PixelWidth, bitmap.PixelHeight));
        if (s < 1.0)
        {
            encoder.BitmapTransform.ScaledWidth = (uint)Math.Max(1, bitmap.PixelWidth * s);
            encoder.BitmapTransform.ScaledHeight = (uint)Math.Max(1, bitmap.PixelHeight * s);
            encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant;
        }
        await encoder.FlushAsync();
        return await ReadAllAsync(stream);
    }

    static async Task<string> OcrRegionAsync(WinPdfPage winPage, Page page, R rect)
    {
        string best = await OcrRegionAtAsync(winPage, page, rect, PictureDpi);
        if (Quality(best) < 0.8)
        {
            string other = await OcrRegionAtAsync(winPage, page, rect, 200);
            if (Score(other) > Score(best)) best = other;
        }
        return CleanOcr(best);
    }

    /// <summary>Letters in real-looking words: higher is a cleaner reading.</summary>
    static int Score(string t) => t.Split((char[])null, StringSplitOptions.RemoveEmptyEntries)
        .Where(w => WordLike.IsMatch(w)).Sum(w => w.Length);

    static double Quality(string t)
    {
        var toks = t.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
        return toks.Length == 0 ? 1 : (double)toks.Count(w => WordLike.IsMatch(w)) / toks.Length;
    }

    /// <summary>Drops lines that are only noise (logos, drawings read as letters).</summary>
    internal static string CleanOcr(string text)
    {
        var keep = new List<string>();
        foreach (var line in text.Split('\n'))
        {
            var toks = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (toks.Length == 0) continue;
            int good = toks.Count(w => WordLike.IsMatch(w));
            if (good > 0 && good * 2 >= toks.Length) keep.Add(line.Trim());
        }
        return string.Join("\n", keep);
    }

    static async Task<string> OcrRegionAtAsync(WinPdfPage winPage, Page page, R rect, double dpi)
    {
        try
        {
            double fx = winPage.Size.Width / page.Width, fy = winPage.Size.Height / page.Height;
            double w = rect.W * dpi / 72, h = rect.H * dpi / 72;
            double max = OcrEngine.MaxImageDimension;
            double s = Math.Min(1.0, max / Math.Max(w, h));
            var options = new Windows.Data.Pdf.PdfPageRenderOptions
            {
                SourceRect = new Windows.Foundation.Rect(rect.X0 * fx, rect.Y0 * fy, rect.W * fx, rect.H * fy),
                DestinationWidth = (uint)Math.Max(1, w * s),
                DestinationHeight = (uint)Math.Max(1, h * s)
            };
            using var stream = new InMemoryRandomAccessStream();
            await winPage.RenderToStreamAsync(stream, options);
            stream.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(stream);
            using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            return await OcrAsync(bitmap);
        }
        catch (Exception e)
        {
            Log.Write("ocr: " + e.Message);
            return "";
        }
    }

    /// <summary>Reads with the English and the Arabic engine and keeps the one that found more of its own script.</summary>
    internal static async Task<string> OcrAsync(SoftwareBitmap bitmap)
    {
        string en = "", ar = "";
        if (Engines.English != null) en = Join(await Engines.English.RecognizeAsync(bitmap));
        if (Engines.Arabic != null) ar = Join(await Engines.Arabic.RecognizeAsync(bitmap));
        int se = Regex.Matches(en, "[A-Za-z0-9]").Count, sa = Regex.Matches(ar, @"[؀-ۿ]").Count;
        return (sa > se ? ar : en).Trim();
    }

    static string Join(OcrResult r) => string.Join("\n", r.Lines.Select(LineText));

    /// <summary>
    /// Arabic lines are put in reading order from where each word sits (right to left);
    /// English words and numbers inside them stay left to right.
    /// </summary>
    static string LineText(OcrLine line)
    {
        var words = line.Words.ToList();
        int ar = words.Sum(w => TextLayer.Arabic.Matches(w.Text).Count);
        int la = words.Sum(w => Regex.Matches(w.Text, "[A-Za-z]").Count);
        if (words.Count < 2 || ar <= la) return line.Text;
        var outp = new List<string>();
        var run = new List<string>();
        foreach (var t in words.OrderByDescending(w => w.BoundingRect.X).Select(w => w.Text))
        {
            if (TextLayer.Arabic.IsMatch(t))
            {
                run.Reverse();
                outp.AddRange(run);
                run.Clear();
                outp.Add(t);
            }
            else run.Add(t);
        }
        run.Reverse();
        outp.AddRange(run);
        return string.Join(" ", outp);
    }

    // ======================= pictures (png, jpg...) =======================
    static async Task<string> ImageAsync(string path, IProgress<Step> progress, CancellationToken ct)
    {
        progress.Report(new Step("Reading the text in the picture", 0.2));
        var file = await StorageFile.GetFileFromPathAsync(path);
        using var stream = await file.OpenReadAsync();
        BitmapDecoder decoder;
        try { decoder = await BitmapDecoder.CreateAsync(stream); }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.Write("picture open: " + e);
            throw new FriendlyException("This picture is damaged or not a real picture file.", e);
        }
        WarmUpAi(ct);
        double max = OcrEngine.MaxImageDimension;
        double s = Math.Min(1.0, max / Math.Max(decoder.PixelWidth, decoder.PixelHeight));
        var transform = new BitmapTransform
        {
            ScaledWidth = (uint)Math.Max(1, decoder.PixelWidth * s),
            ScaledHeight = (uint)Math.Max(1, decoder.PixelHeight * s),
            InterpolationMode = BitmapInterpolationMode.Fant
        };
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            transform, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);
        ct.ThrowIfCancellationRequested();
        string text = null, description = null;
        if (Ai.Enabled)
        {
            try
            {
                if (await Ai.EnsureRunningAsync(progress, ct))
                {
                    progress.Report(new Step("Reading the picture with the AI", 0.4));
                    text = await Ai.AskAsync(Ai.FullPrompt, await PngAsync(bitmap), 1500, false, ct);
                    if (Settings.Describe)
                    {
                        progress.Report(new Step("Describing the picture", 0.7));
                        var (d, _) = Ai.SplitPicture(await Ai.AskAsync(Ai.PicturePrompt, await PngAsync(bitmap, 1280), 900, false, ct), true);
                        if (d.Length > 0) description = d;
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception e) { Log.Write("AI: " + e.Message); text = null; }
        }
        bool byAi = text != null;
        text ??= CleanOcr(await OcrAsync(bitmap));
        progress.Report(new Step("Saving", 1));
        return $"{Mark}\n\n# {Path.GetFileNameWithoutExtension(path)}\n\n" +
               (byAi ? $"> Text read from the picture by the built-in AI ({Ai.ModelName}) - verify.\n\n"
                     : "> Text read from the picture by the quick built-in reader - verify.\n\n") +
               (description != null ? $"### Description (AI - verify)\n\n{description}\n\n### Text\n\n" : "") +
               (text.Length > 0 ? text : "(no readable text in this picture)") + "\n";
    }
}

/// <summary>The recognizers built into Windows, created once.</summary>
static class Engines
{
    static OcrEngine english, arabic;
    static bool inkTried, inkAvailable, ocrTried;
    static string inkName;

    public static bool InkAvailable
    {
        get
        {
            if (inkTried) return inkAvailable;
            inkTried = true;
            try
            {
                var all = new InkRecognizerContainer().GetRecognizers();
                var en = all.FirstOrDefault(r => r.Name.Contains("English (US)")) ?? all.FirstOrDefault(r => r.Name.Contains("English"));
                inkName = en?.Name;
                inkAvailable = all.Count > 0;
            }
            catch (Exception e) { Log.Write("handwriting recognizer: " + e.Message); }
            return inkAvailable;
        }
    }

    public static InkRecognizerContainer NewInk()
    {
        var c = new InkRecognizerContainer();
        if (inkName != null)
        {
            var en = c.GetRecognizers().FirstOrDefault(r => r.Name == inkName);
            if (en != null) c.SetDefaultRecognizer(en);
        }
        return c;
    }

    public static OcrEngine English { get { MakeOcr(); return english; } }
    public static OcrEngine Arabic { get { MakeOcr(); return arabic; } }

    static void MakeOcr()
    {
        if (ocrTried) return;
        ocrTried = true;
        try
        {
            foreach (var l in OcrEngine.AvailableRecognizerLanguages)
            {
                if (english == null && l.LanguageTag.StartsWith("en")) english = OcrEngine.TryCreateFromLanguage(l);
                if (arabic == null && l.LanguageTag.StartsWith("ar")) arabic = OcrEngine.TryCreateFromLanguage(l);
            }
            english ??= OcrEngine.TryCreateFromUserProfileLanguages();
        }
        catch (Exception e) { Log.Write("ocr engines: " + e.Message); }
    }
}
