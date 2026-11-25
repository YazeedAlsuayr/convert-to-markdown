// PDF (and picture) -> Markdown, using only what is built into Windows:
//   typed text, highlights   - read straight from the PDF (exact)
//   handwriting              - Windows handwriting recognition, from the pen strokes stored in the PDF
//   text inside pictures     - Windows OCR (English and Arabic)

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
        if (!File.Exists(path)) throw new FileNotFoundException("The file was not found.");
        string ext = Path.GetExtension(path).ToLowerInvariant();
        string body;
        if (ext == ".pdf") body = await PdfAsync(path, progress, ct);
        else if (ImageTypes.Contains(ext)) body = await ImageAsync(path, progress, ct);
        else if (OfficeConverter.Types.Contains(ext)) body = await OfficeConverter.ConvertAsync(path, progress, ct);
        else if (ext is ".doc" or ".ppt" or ".xls")
            throw new NotSupportedException("This is the old Office format. Open it and use Save As .docx, .pptx or .xlsx, then convert that.");
        else throw new NotSupportedException("This kind of file isn't supported.");

        string output = TargetPath(path);
        string tmp = Path.Combine(Program.CacheDir, $"{Environment.TickCount64}_{Path.GetFileName(output)}");
        await File.WriteAllTextAsync(tmp, body, new UTF8Encoding(false), CancellationToken.None);
        ct.ThrowIfCancellationRequested();
        if (File.Exists(output)) File.Delete(output);     // only ever our own earlier output (see TargetPath)
        File.Move(tmp, output);
        return output;
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
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var storage = await StorageFile.GetFileFromPathAsync(path);
        var winDoc = await WinPdfDocument.LoadFromFileAsync(storage);
        using var doc = PigDocument.Open(path);
        int n = doc.NumberOfPages;

        // first pass: the typed text of every page (also gives the lecture's own words)
        var allLines = new List<TextLine>[n + 1];
        for (int i = 1; i <= n; i++)
        {
            ct.ThrowIfCancellationRequested();
            try { allLines[i] = TextLayer.Build(doc.GetPage(i)); }
            catch (Exception e) { allLines[i] = new List<TextLine>(); Log.Write($"page {i} text: {e.Message}"); }
        }
        var vocab = new Vocabulary(allLines.Skip(1).SelectMany(ls => ls.Select(l => l.Text)));

        var md = new StringBuilder();
        md.Append(Mark).Append("\n\n# ").Append(Path.GetFileNameWithoutExtension(path)).Append("\n\n");
        bool ai = await Ai.EnsureRunningAsync(progress, ct);
        if (!ai) Log.Write("AI not used: " + Ai.Status);
        md.Append(ai
            ? "> Typed text and highlights come from the PDF itself (exact). " +
              (Settings.Describe ? "Descriptions and the text in pictures are written" : "The text in pictures is read") +
              $" by the built-in AI ({Ai.ModelName}) and handwriting by Windows handwriting recognition; these CAN contain errors. " +
              "'possible' words are guesses from the lecture's own vocabulary.\n"
            : "> Typed text and highlights come from the PDF itself (exact). Handwriting is read by Windows handwriting " +
              "recognition and picture text by Windows OCR, and CAN contain errors. 'possible' words are guesses " +
              "from the lecture's own vocabulary.\n");
        if (ai) md.Append($"> The AI ran on the {Ai.Device}.\n");
        else md.Append($"> The AI was not used: {Ai.Status}.\n");

        for (int i = 1; i <= n; i++)
        {
            ct.ThrowIfCancellationRequested();
            string here = $"Page {i} of {n}";
            progress.Report(new Step(here + "  ·  reading text", (i - 1.0) / n));
            var page = doc.GetPage(i);
            var lines = allLines[i];
            string typed = TextLayer.PageText(lines);
            bool picturePage = typed.Length < 60;
            var P = new R(0, 0, page.Width, page.Height);

            md.Append($"\n\n---\n\n## Page {i}\n");
            if (!picturePage)
                md.Append("\n### Typed text (exact)\n\n").Append(typed).Append('\n');

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
                    progress.Report(new Step(here + "  ·  describing the layout", (i - 0.9) / n));
                    string layout = await AiRegionAsync(winPage, page, P, Ai.DescribePrompt, 110, 200, ct);
                    if (!string.IsNullOrWhiteSpace(layout))
                        description.Add("**Layout:** " + Regex.Replace(layout, @"\s*\n\s*", " ").Trim());
                }

                if (picturePage)
                {
                    if (description.Count > 0)
                        md.Append("\n### Description (AI - verify)\n\n").Append(string.Join("\n\n", description)).Append('\n');
                    progress.Report(new Step(here + "  ·  reading the page images", (i - 0.8) / n));
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
                        progress.Report(new Step($"{here}  ·  reading picture {k + 1} of {pics.Count}", (i - 0.8 + 0.3 * k / pics.Count) / n));
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
                        md.Append(anyOcr ? "\n### Text in pictures (Windows OCR - verify)\n" : "\n### Text in pictures (AI - verify)\n")
                          .Append(string.Concat(texts.Select(t => "\n" + t + "\n")));
                }
            }

            // --- handwriting ---
            if (pens.Count > 0)
            {
                var (writing, lineMarks) = SplitMarks(pens);
                var notesOnPage = Cluster(writing);
                md.Append("\n### Handwriting (pen) - verify\n");
                bool wroteNote = false;
                var marks = new List<string>();
                for (int k = 0; k < notesOnPage.Count; k++)
                {
                    ct.ThrowIfCancellationRequested();
                    progress.Report(new Step($"{here}  ·  reading handwriting {k + 1} of {notesOnPage.Count}", (i - 0.4 + 0.4 * k / notesOnPage.Count) / n));
                    var note = notesOnPage[k];
                    string text = await RecognizeInkAsync(note, vocab);
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

    static bool Near(R a, R b)
    {
        double gx = Math.Max(Math.Max(b.X0 - a.X1, a.X0 - b.X1), 0);
        double ov = Math.Min(a.Y1, b.Y1) - Math.Max(a.Y0, b.Y0);
        double h = Math.Min(a.H, b.H);
        if (gx == 0 && h > 0 && ov >= 0.5 * h) return true;
        return gx <= GapX && h > 0 && ov >= RowOverlap * h;
    }

    static List<Note> Cluster(List<Note> notes)
    {
        notes = notes.ToList();
        bool merged = true;
        while (merged)
        {
            merged = false;
            for (int i = 0; i < notes.Count && !merged; i++)
                for (int j = i + 1; j < notes.Count && !merged; j++)
                    if (Near(notes[i].Box, notes[j].Box))
                    {
                        notes[i].Strokes.AddRange(notes[j].Strokes);
                        notes[i].Box = notes[i].Box.Union(notes[j].Box);
                        notes.RemoveAt(j);
                        merged = true;
                    }
        }
        return notes.OrderBy(n => Math.Round(n.Box.Y0 / 25)).ThenBy(n => n.Box.X0).ToList();
    }

    // ---------------- handwriting ----------------
    static async Task<string> RecognizeInkAsync(Note note, Vocabulary vocab)
    {
        if (!Engines.InkAvailable) return "(Windows handwriting recognition is not installed)";
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
                // the recognizer's own second guesses: prefer one that is a word from the lecture or medicine
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
            return await Ai.AskAsync(prompt, png, maxTokens, false, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e)
        {
            Log.Write("AI: " + e.Message);
            return null;
        }
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
        var decoder = await BitmapDecoder.CreateAsync(stream);
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
                     : "> Text read from the picture by Windows OCR - verify.\n\n") +
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
