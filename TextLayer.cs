// The typed text of a PDF page, rebuilt from glyph positions.
// Glyphs are put in screen (visual) order, left to right, so Arabic is always handled the same way:
// it is turned back into reading order here, whatever order the PDF stored it in.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using UglyToad.PdfPig.Content;

namespace ConvertToMarkdown;

readonly record struct R(double X0, double Y0, double X1, double Y1)
{
    public double W => X1 - X0;
    public double H => Y1 - Y0;
    public double Area => Math.Max(W, 0) * Math.Max(H, 0);
    public R Union(R b) => new(Math.Min(X0, b.X0), Math.Min(Y0, b.Y0), Math.Max(X1, b.X1), Math.Max(Y1, b.Y1));
    public R Grow(double m) => new(X0 - m, Y0 - m, X1 + m, Y1 + m);
    public double Overlap(R b)
    {
        double w = Math.Min(X1, b.X1) - Math.Max(X0, b.X0), h = Math.Min(Y1, b.Y1) - Math.Max(Y0, b.Y0);
        return Math.Max(w, 0) * Math.Max(h, 0);
    }
}

class Ch
{
    public string C;
    public double X0, X1, Y0, Y1, Base, Size;
    public int Script;                           // 1 = superscript, -1 = subscript (raised or lowered smaller text)
    public bool IsSpace => string.IsNullOrWhiteSpace(C);
}

class TextLine
{
    public List<Ch> Chars = new();               // visual order, spaces included
    public double Base, Size;
    public R Box;
    public string Text => TextLayer.ToLogical(TextLayer.Joined(Chars));
}

static class TextLayer
{
    public static readonly Regex Arabic = new(@"[؀-ۿݐ-ݿﭐ-﷿ﹰ-﻿]");
    static readonly Regex Latin = new(@"[A-Za-z]");

    // ---------------- glyphs -> lines ----------------
    public static List<TextLine> Build(Page page)
    {
        double H = page.Height;
        var glyphs = new List<Ch>();
        foreach (var l in page.Letters)
        {
            if (string.IsNullOrEmpty(l.Value)) continue;
            // slanted text (a diagonal "DRAFT" or "CONFIDENTIAL" watermark) is not part of the reading text
            double bx = l.EndBaseLine.X - l.StartBaseLine.X, by = l.EndBaseLine.Y - l.StartBaseLine.Y;
            double blen = Math.Sqrt(bx * bx + by * by);
            if (blen > 0.01 && Math.Abs(bx) > 0.26 * blen && Math.Abs(by) > 0.26 * blen) continue;
            double size = Math.Max(l.PointSize, l.GlyphRectangle.Height);
            if (size < 0.5) size = 1;
            double x0 = l.StartBaseLine.X, x1 = l.EndBaseLine.X;
            if (x1 < x0) (x0, x1) = (x1, x0);
            if (x1 - x0 < 0.01) { x0 = l.GlyphRectangle.Left; x1 = l.GlyphRectangle.Right; }
            double b = H - l.StartBaseLine.Y;
            glyphs.Add(new Ch { C = l.Value, X0 = x0, X1 = x1, Base = b, Size = size, Y0 = b - 0.8 * size, Y1 = b + 0.2 * size });
        }
        glyphs = AttachMarks(glyphs);

        // rows of glyphs sharing a baseline
        var rows = new List<List<Ch>>();
        foreach (var g in glyphs.OrderBy(g => g.Base))
        {
            var last = rows.Count > 0 ? rows[^1] : null;
            if (last != null && Math.Abs(g.Base - last[0].Base) < 0.4 * Math.Min(g.Size, last[0].Size))
                last.Add(g);
            else
                rows.Add(new List<Ch> { g });
        }
        MergeScripts(rows);
        AddDrawnBullets(page, rows, H);

        var lines = new List<TextLine>();
        foreach (var row in rows)
        {
            var sorted = row.OrderBy(g => g.X0).ToList();
            TextLine cur = null;
            Ch prev = null;
            foreach (var g in sorted)
            {
                if (prev != null && g.C == prev.C && Math.Abs(g.X0 - prev.X0) < 0.1 * g.Size)
                    continue;                                   // the same glyph drawn twice (shadow, fake bold)
                double gap = prev == null ? 0 : g.X0 - prev.X1;
                // a big gap, or a jump in font size with some gap, starts a new text box;
                // a bullet or list number stays with the text right after it (but not across a table column)
                double jump = prev == null ? 0 : Math.Abs(g.Size - prev.Size) / Math.Max(g.Size, prev.Size);
                bool scripted = prev != null && (g.Script != 0 || prev.Script != 0);
                bool sizeJump = prev != null && !scripted && !g.IsSpace && !prev.IsSpace && (jump > 0.4 || (jump > 0.25 && gap > 0.6 * g.Size));
                bool keepWithMarker = cur != null && IsListMarker(cur) && gap < 3 * g.Size;
                if (cur == null || ((gap > 1.5 * g.Size || sizeJump) && !keepWithMarker))
                {
                    cur = new TextLine();
                    lines.Add(cur);
                    prev = null;
                }
                else if (prev != null && !g.IsSpace && !prev.IsSpace && gap > (IsArabic(g) && IsArabic(prev) ? 0.3 : 0.18) * Math.Max(g.Size, prev.Size) * (scripted ? 0.6 : 1))
                    cur.Chars.Add(new Ch { C = " ", X0 = prev.X1, X1 = g.X0, Y0 = g.Y0, Y1 = g.Y1, Base = g.Base, Size = g.Size });
                if (g.IsSpace && (prev == null || prev.IsSpace))
                    continue;                                   // no double spaces
                cur.Chars.Add(g);
                prev = g;
            }
        }

        foreach (var ln in lines)
        {
            while (ln.Chars.Count > 0 && ln.Chars[^1].IsSpace) ln.Chars.RemoveAt(ln.Chars.Count - 1);
            if (ln.Chars.Count == 0) continue;
            ln.Box = new R(ln.Chars.Min(c => c.X0), ln.Chars.Min(c => c.Y0), ln.Chars.Max(c => c.X1), ln.Chars.Max(c => c.Y1));
            var main = ln.Chars.Where(c => c.Script == 0).ToList();
            if (main.Count == 0) main = ln.Chars;
            ln.Base = main[0].Base;
            var sizes = main.Select(c => c.Size).OrderBy(s => s).ToList();
            ln.Size = sizes[sizes.Count / 2];
        }
        lines.RemoveAll(l => l.Chars.Count == 0);
        return lines.OrderBy(l => Math.Round(l.Box.Y0 / 6)).ThenBy(l => l.Box.X0).ToList();
    }

    static bool IsArabic(Ch c) => Arabic.IsMatch(c.C);

    static bool IsMark(string s) => s.Length > 0 && s.All(c =>
        CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark);

    /// <summary>
    /// Accents and Arabic marks (fatha, tanween, shadda…) are separate glyphs in a PDF. Each one is joined to the letter
    /// it sits on, so it can never end up after a full stop or on the wrong letter.
    /// </summary>
    static List<Ch> AttachMarks(List<Ch> glyphs)
    {
        var marks = glyphs.Where(g => IsMark(g.C)).ToList();
        if (marks.Count == 0) return glyphs;
        var bases = glyphs.Where(g => !IsMark(g.C) && !g.IsSpace).ToList();
        foreach (var m in marks)
        {
            double mc = (m.X0 + m.X1) / 2;
            Ch best = null;
            double bestScore = double.MaxValue;
            foreach (var b in bases)
            {
                if (Math.Abs(b.Base - m.Base) > 0.6 * Math.Max(b.Size, m.Size)) continue;
                double score = mc < b.X0 ? b.X0 - mc : mc > b.X1 ? mc - b.X1 : 0;   // 0 when the mark is over the letter
                if (score < bestScore) { bestScore = score; best = b; }
            }
            if (best != null && bestScore < 0.6 * best.Size) best.C += m.C;
            else bases.Add(m);                                   // a lone mark: keep it as it is
        }
        var kept = new HashSet<Ch>(bases);
        return glyphs.Where(g => kept.Contains(g) || g.IsSpace).ToList();
    }

    /// <summary>
    /// Raised or lowered smaller text right next to normal text (x², min(…)^α, HCO₃⁻) belongs to that line,
    /// not to a line of its own above or below.
    /// </summary>
    static void MergeScripts(List<List<Ch>> rows)
    {
        for (int r = 0; r < rows.Count; r++)
        {
            var row = rows[r];
            if (row.Count == 0) continue;
            double small = Median(row.Select(g => g.Size));
            // the row's glyphs, in clusters of touching glyphs
            var clusters = new List<List<Ch>>();
            foreach (var g in row.OrderBy(g => g.X0))
            {
                if (clusters.Count > 0 && g.X0 - clusters[^1][^1].X1 < 0.8 * small) clusters[^1].Add(g);
                else clusters.Add(new List<Ch> { g });
            }
            foreach (var c in clusters)
            {
                if (c.Count(g => !g.IsSpace) > 4) continue;              // x², ^α, ^age, HCO₃⁻ are short; a typed note is not
                double cx0 = c.Min(g => g.X0), cx1 = c.Max(g => g.X1), cb = c[0].Base, cs = Median(c.Select(g => g.Size));
                foreach (int k in new[] { r - 1, r + 1, r - 2, r + 2 })
                {
                    if (k < 0 || k >= rows.Count || rows[k].Count == 0) continue;
                    var main = rows[k];
                    double ms = Median(main.Select(g => g.Size));
                    if (cs > 0.87 * ms) continue;                          // not smaller: a normal line
                    double d = cb - main[0].Base;                           // < 0: raised, > 0: lowered
                    int script = d >= -0.65 * ms && d <= -0.15 * ms ? 1 : d >= 0.08 * ms && d <= 0.45 * ms ? -1 : 0;
                    if (script == 0) continue;
                    bool touches = main.Any(g => !g.IsSpace && ((cx0 - g.X1 > -0.15 * ms && cx0 - g.X1 < 0.35 * ms) || (g.X0 - cx1 > -0.15 * ms && g.X0 - cx1 < 0.35 * ms)));
                    bool overlaps = main.Any(g => !g.IsSpace && Math.Min(cx1, g.X1) - Math.Max(cx0, g.X0) > 0.15 * ms);
                    if (!touches || overlaps) continue;                     // text over or under other text is a note, not a superscript
                    foreach (var g in c)
                    {
                        g.Script = script;
                        g.Base = main[0].Base;
                        main.Add(g);
                        row.Remove(g);
                    }
                    break;
                }
            }
        }
        rows.RemoveAll(r => r.Count == 0);
    }

    static double Median(IEnumerable<double> xs)
    {
        var l = xs.OrderBy(x => x).ToList();
        return l.Count == 0 ? 0 : l[l.Count / 2];
    }

    /// <summary>
    /// Some PDFs (web pages, Word, Chrome) draw list bullets as small dots and circles instead of "•" characters.
    /// A small round or square shape just before the start of a line becomes a bullet character.
    /// </summary>
    static void AddDrawnBullets(Page page, List<List<Ch>> rows, double H)
    {
        IEnumerable<UglyToad.PdfPig.Graphics.PdfPath> paths;
        try { paths = page.ExperimentalAccess.Paths; }
        catch { return; }
        if (paths == null) return;
        int added = 0;
        foreach (var path in paths)
        {
            if (added > 200) break;
            try
            {
                if (path.IsClipping || (!path.IsFilled && !path.IsStroked)) continue;
                var bb = path.GetBoundingRectangle();
                if (bb == null) continue;
                var b = bb.Value;
                double w = b.Width, h = b.Height;
                if (w < 1.2 || h < 1.2 || w > 9 || h > 9 || w > 1.7 * h || h > 1.7 * w) continue;
                double x0 = Math.Min(b.Left, b.Right), x1 = Math.Max(b.Left, b.Right);
                double cy = H - (b.Top + b.Bottom) / 2;
                foreach (var row in rows)
                {
                    if (row.Count == 0) continue;
                    double size = Median(row.Select(g => g.Size)), bs = row[0].Base;
                    if (cy < bs - 0.75 * size || cy > bs + 0.05 * size) continue;        // vertically on the text line
                    if (w > 0.7 * size || h > 0.7 * size) continue;                       // a bullet is smaller than the letters
                    var after = row.Where(g => !g.IsSpace && g.X0 >= x1 - 0.5).OrderBy(g => g.X0).FirstOrDefault();
                    var before = row.Where(g => !g.IsSpace && g.X1 <= x0 + 0.5).OrderByDescending(g => g.X1).FirstOrDefault();
                    string mark = path.IsFilled ? "•" : "◦";
                    bool leftBullet = after != null && after.X0 - x1 <= 3 * size && (before == null || before.X1 <= x0 - 2.5 * size);
                    // right-to-left text (Arabic): the bullet is at the right end of the line
                    bool rightBullet = !leftBullet && before != null && IsArabic(before) && x0 - before.X1 <= 3 * size
                                       && (after == null || after.X0 >= x1 + 2.5 * size);
                    if (leftBullet)
                        row.Add(new Ch { C = mark, X0 = x0, X1 = x1, Base = bs, Size = size, Y0 = bs - 0.8 * size, Y1 = bs + 0.2 * size });
                    else if (rightBullet)                                   // placed right after the text, so it stays on the same line
                        row.Add(new Ch { C = mark, X0 = before.X1 + 0.3 * size, X1 = before.X1 + 0.3 * size + w, Base = bs, Size = size, Y0 = bs - 0.8 * size, Y1 = bs + 0.2 * size });
                    else continue;
                    added++;
                    break;
                }
            }
            catch { }
        }
    }

    /// <summary>The line's characters, with superscripts as ⁽…⁾ characters or ^ and subscripts as ₍…₎ or _.</summary>
    public static string Joined(List<Ch> chars)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < chars.Count;)
        {
            if (chars[i].Script == 0) { sb.Append(chars[i].C); i++; continue; }
            int script = chars[i].Script, j = i;
            var run = new StringBuilder();
            while (j < chars.Count && chars[j].Script == script) { run.Append(chars[j].C); j++; }
            string t = run.ToString().Trim();
            string map = script > 0 ? Sup : Sub;
            if (t.Length > 0 && t.All(c => Plain.IndexOf(c) >= 0 || (script > 0 && c is 'n' or 'i')))
                sb.Append(string.Concat(t.Select(c => c == 'n' ? 'ⁿ' : c == 'i' ? 'ⁱ' : map[Plain.IndexOf(c)])));
            else if (t.Length > 0)
            {
                bool more = j < chars.Count && !chars[j].IsSpace;
                sb.Append(script > 0 ? "^" : "_").Append(t.Length > 1 && more ? "(" + t + ")" : t);
            }
            i = j;
        }
        return sb.ToString();
    }
    const string Plain = "0123456789+-=()", Sup = "⁰¹²³⁴⁵⁶⁷⁸⁹⁺⁻⁼⁽⁾", Sub = "₀₁₂₃₄₅₆₇₈₉₊₋₌₍₎";

    /// <summary>"•", "-", "1.", "a)" and the like: a list marker waiting for its text.</summary>
    static bool IsListMarker(TextLine ln)
    {
        var cs = ln.Chars.Where(c => !c.IsSpace).ToList();
        if (cs.Count == 0 || cs.Count > 3) return false;
        int alnum = cs.Count(c => char.IsLetterOrDigit(c.C, 0));
        string last = cs[^1].C;
        return alnum == 0 || (alnum <= 2 && cs.Count == alnum + 1 && (last == "." || last == ")"));   // "1." "a)" "12."
    }

    // ---------------- the page's typed text, in reading order ----------------
    /// <summary>
    /// Lines are first grouped into text boxes (stacked lines, overlapping sideways, same font size).
    /// Boxes placed side by side on shared baselines are then read as one of two things:
    /// flowing text (newspaper columns, a question | answer table): each box is read whole, left to right, row by row;
    /// or a grid of short cells (a data table): written as a Markdown table.
    /// </summary>
    public static string PageText(List<TextLine> lines)
    {
        var ls = lines.Where(l => l.Text.Trim().Length > 0).ToList();
        if (ls.Count == 0) return "";
        var blocks = new List<List<int>>();
        for (int i = 0; i < ls.Count; i++)
        {
            var ln = ls[i];
            int best = -1;
            double bestOverlap = 0;
            for (int b = 0; b < blocks.Count; b++)
            {
                var last = ls[blocks[b][^1]];
                double size = Math.Min(ln.Size, last.Size);
                double gap = ln.Box.Y0 - last.Box.Y1;
                if (gap < -0.3 * size || gap > 1.3 * size) continue;
                if (Math.Abs(ln.Size - last.Size) > 0.3 * Math.Max(ln.Size, last.Size)) continue;
                double x0 = blocks[b].Min(k => ls[k].Box.X0), x1 = blocks[b].Max(k => ls[k].Box.X1);
                double overlap = Math.Min(ln.Box.X1, x1) - Math.Max(ln.Box.X0, x0);
                if (overlap > bestOverlap) { bestOverlap = overlap; best = b; }
            }
            if (best < 0) { best = blocks.Count; blocks.Add(new List<int>()); }
            blocks[best].Add(i);
        }

        bool SameBase(TextLine a, TextLine b) => Math.Abs(a.Base - b.Base) < 0.3 * Math.Min(a.Size, b.Size);
        R BoxOf(IEnumerable<int> idx) => idx.Select(k => ls[k].Box).Aggregate((a, b) => a.Union(b));

        // boxes side by side whose lines share baselines belong together
        var parent = Enumerable.Range(0, blocks.Count).ToArray();
        int Find(int a) { while (parent[a] != a) a = parent[a] = parent[parent[a]]; return a; }
        for (int a = 0; a < blocks.Count; a++)
            for (int b = a + 1; b < blocks.Count; b++)
            {
                int aligned = blocks[a].Count(i => blocks[b].Any(j => SameBase(ls[i], ls[j])));
                int smaller = Math.Min(blocks[a].Count, blocks[b].Count);
                if (smaller >= 2 && aligned >= Math.Max(2, 0.5 * smaller)) parent[Find(a)] = Find(b);
            }
        // tall boxes of text side by side, overlapping in height, are columns of one layout even when their
        // baselines do not line up (one column starts after a paragraph gap)
        for (int a = 0; a < blocks.Count; a++)
            for (int b = a + 1; b < blocks.Count; b++)
            {
                if (blocks[a].Count < 3 || blocks[b].Count < 3 || Find(a) == Find(b)) continue;
                R ba = BoxOf(blocks[a]), bb = BoxOf(blocks[b]);
                double sa = Median(blocks[a].Select(k => ls[k].Size)), sb = Median(blocks[b].Select(k => ls[k].Size));
                if (Math.Abs(sa - sb) > 0.15 * Math.Max(sa, sb)) continue;
                double xGap = Math.Max(ba.X0, bb.X0) - Math.Min(ba.X1, bb.X1);
                double yOverlap = Math.Min(ba.Y1, bb.Y1) - Math.Max(ba.Y0, bb.Y0);
                if (xGap > 0 && xGap < 4 * Math.Max(sa, sb) && yOverlap > 0.5 * Math.Min(ba.H, bb.H)
                    && (Paragraphish(blocks[a].Select(k => ls[k]).ToList(), 0.55) || Paragraphish(blocks[b].Select(k => ls[k]).ToList(), 0.55)))
                    parent[Find(a)] = Find(b);
            }
        // one-line boxes join a table they line up with (a cell on the same baseline as a row),
        // or that they sit inside (a header cell centred over two rows, a spanning header just above)
        for (int pass = 0; pass < 2; pass++)
            for (int a = 0; a < blocks.Count; a++)
            {
                if (blocks[a].Count != 1) continue;
                var one = ls[blocks[a][0]];
                foreach (var grp in Enumerable.Range(0, blocks.Count).GroupBy(Find).ToList())
                {
                    if (grp.Key == Find(a) || grp.Count() < 2) continue;
                    var members = grp.SelectMany(b => blocks[b]).ToList();
                    var box = BoxOf(members);
                    double size = Math.Min(one.Size, members.Min(k => ls[k].Size));
                    bool sideways = one.Box.X1 > box.X0 - 8 * size && one.Box.X0 < box.X1 + 8 * size;
                    bool inRow = sideways && members.Any(k => SameBase(one, ls[k]));
                    bool notHeading = one.Size <= 1.15 * Median(members.Select(k => ls[k].Size));
                    bool inside = notHeading && one.Box.X0 >= box.X0 - size && one.Box.X1 <= box.X1 + size &&
                                  one.Box.Y0 >= box.Y0 - 2.5 * size && one.Box.Y1 <= box.Y1 + 0.5 * size;
                    if (inRow || inside) { parent[Find(a)] = Find(grp.First()); break; }
                }
            }

        var groups = Enumerable.Range(0, blocks.Count).GroupBy(Find)
            .Select(g => g.OrderBy(b => blocks[b].Min()).ToList())
            .OrderBy(g => Math.Round(BoxOf(g.SelectMany(b => blocks[b])).Y0 / 12))
            .ThenBy(g => BoxOf(g.SelectMany(b => blocks[b])).X0)
            .ToList();

        var parts = new List<string>();
        foreach (var g in groups)
        {
            var gblocks = g.Select(b => blocks[b].Select(k => ls[k]).ToList()).ToList();
            if (gblocks.Count == 1) parts.Add(RenderBlock(gblocks[0]));
            else if (IsFlowingText(gblocks)) parts.Add(RenderFlow(gblocks));
            else parts.Add(RenderGrid(gblocks.SelectMany(x => x).ToList()));
        }
        return string.Join("\n\n", parts.Where(p => p.Length > 0)).Trim();
    }

    /// <summary>A box of sentences: wrapped lines that fill its width.</summary>
    static bool Paragraphish(List<TextLine> bl, double fill = 0.7)
    {
        if (bl.Count < 3) return false;
        double w = bl.Max(l => l.Box.W);
        int n = bl.Take(bl.Count - 1).Count(l => l.Box.W >= fill * w && l.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 3);
        return n >= 0.6 * (bl.Count - 1);
    }

    /// <summary>
    /// Columns of sentences or of bulleted lists, or a few small boxes of wrapped phrases (a diagram),
    /// rather than a grid of short cells.
    /// </summary>
    static bool IsFlowingText(List<List<TextLine>> gblocks)
    {
        int textBlocks = 0, full = 0;
        foreach (var bl in gblocks)
        {
            if (bl.Count < 3) continue;
            textBlocks++;
            int bullets = bl.Count(l => Bullet.IsMatch(l.Text.Trim()));
            if (Paragraphish(bl) || bullets >= 0.5 * bl.Count) full++;
        }
        if (textBlocks > 0 && full * 2 >= textBlocks) return true;
        // a few small boxes whose lines are spaced like normal text (not like table rows with padding)
        var multi = gblocks.Where(b => b.Count >= 2).ToList();
        if (gblocks.Count <= 3 && multi.Count > 0 && gblocks.All(b => b.Count <= 5))
        {
            var steps = multi.SelectMany(b => b.Zip(b.Skip(1), (x, y) => (y.Base - x.Base) / Math.Max(x.Size, 1))).ToList();
            if (steps.Count > 0 && Median(steps) <= 1.45) return true;
        }
        return false;
    }

    /// <summary>Side-by-side boxes read whole: row by row (boxes overlapping in height), left to right within a row.</summary>
    static string RenderFlow(List<List<TextLine>> gblocks)
    {
        var items = gblocks.Select(b => (lines: b, box: b.Select(l => l.Box).Aggregate((x, y) => x.Union(y)))).ToList();
        // rows: boxes that overlap in height
        var rows = new List<List<(List<TextLine> lines, R box)>>();
        foreach (var it in items.OrderBy(i => i.box.Y0))
        {
            var row = rows.FirstOrDefault(r => r.Any(o => it.box.Y0 < o.box.Y1 - 2 && o.box.Y0 < it.box.Y1 - 2));
            if (row == null) rows.Add(new List<(List<TextLine>, R)> { it });
            else row.Add(it);
        }
        var sb = new StringBuilder();
        foreach (var row in rows.OrderBy(r => r.Min(i => i.box.Y0)))
        {
            // columns inside the row: boxes overlapping sideways share a column
            var cols = new List<List<(List<TextLine> lines, R box)>>();
            foreach (var it in row.OrderBy(i => i.box.X0))
            {
                var col = cols.FirstOrDefault(c => c.Any(o => Math.Min(it.box.X1, o.box.X1) - Math.Max(it.box.X0, o.box.X0) > 0.3 * Math.Min(it.box.W, o.box.W)));
                if (col == null) cols.Add(new List<(List<TextLine>, R)> { it });
                else col.Add(it);
            }
            foreach (var col in cols.OrderBy(c => c.Min(i => i.box.X0)))
                foreach (var it in col.OrderBy(i => i.box.Y0))
                {
                    if (sb.Length > 0) sb.Append("\n\n");
                    sb.Append(RenderBlock(it.lines));
                }
        }
        return sb.ToString();
    }

    static readonly Regex Bullet = new(@"^([•◦▪▫‣●○■□∙·\*\-–])\s+(\S.*)$");
    static readonly Regex Numbered = new(@"^(\d{1,3}|[a-zA-Z]|[ivxIVX]{1,4})[.)]\s+\S");

    /// <summary>A box of stacked lines: paragraphs, with bullets as "- " and nesting kept by indentation.</summary>
    static string RenderBlock(List<TextLine> block)
    {
        // Arabic lists are indented from the right edge: measure the indent from there
        bool rtl = block.Count(l => Regex.IsMatch(l.Text, @"[\u0600-\u06FF]")) * 2 > block.Count;
        double In(TextLine l) => rtl ? -l.Box.X1 : l.Box.X0;
        var listX = block.Where(l => Bullet.IsMatch(l.Text.Trim()) || Numbered.IsMatch(l.Text.Trim()))
                         .Select(In).OrderBy(x => x).ToList();
        var levels = new List<double>();
        foreach (var x in listX)
            if (levels.Count == 0 || x - levels[^1] > 0.8 * block[0].Size) levels.Add(x);
        int Level(double x) { int k = 0; for (int i = 0; i < levels.Count; i++) if (x >= levels[i] - 0.4 * block[0].Size) k = i; return Math.Min(k, 5); }

        var sb = new StringBuilder();
        TextLine prev = null;
        int itemLevel = -1;
        foreach (var ln in block)
        {
            string t = ln.Text.Trim();
            var bm = Bullet.Match(t);
            string outLine;
            if (bm.Success)
            {
                itemLevel = Level(In(ln));
                outLine = new string(' ', 2 * itemLevel) + "- " + bm.Groups[2].Value;
            }
            else if (Numbered.IsMatch(t))
            {
                itemLevel = Level(In(ln));
                outLine = new string(' ', 3 * itemLevel) + t;
            }
            else if (itemLevel >= 0 && prev != null && In(ln) > levels[Math.Min(itemLevel, levels.Count - 1)] + 0.5 * ln.Size
                     && ln.Box.Y0 - prev.Box.Y1 < 0.6 * ln.Size)
                outLine = new string(' ', 2 * itemLevel + 2) + t;          // the wrapped rest of a list item
            else
            {
                itemLevel = -1;
                outLine = t;
            }
            if (prev != null)
                sb.Append(ln.Box.Y0 - prev.Box.Y1 > 0.9 * Math.Min(ln.Size, prev.Size) && itemLevel < 0 ? "\n\n" : "\n");
            sb.Append(outLine);
            prev = ln;
        }
        return sb.ToString();
    }

    /// <summary>A grid of short cells as a Markdown table. Merged cells stay in the first column or row they cover.</summary>
    static string RenderGrid(List<TextLine> cells)
    {
        double size = Median(cells.Select(c => c.Size));
        // rows by vertical centre
        var rows = new List<List<TextLine>>();
        foreach (var c in cells.OrderBy(c => (c.Box.Y0 + c.Box.Y1) / 2))
        {
            double cy = (c.Box.Y0 + c.Box.Y1) / 2;
            if (rows.Count > 0 && cy - rows[^1].Average(x => (x.Box.Y0 + x.Box.Y1) / 2) < 0.6 * size) rows[^1].Add(c);
            else rows.Add(new List<TextLine> { c });
        }
        // columns from the fullest row
        var model = rows.OrderByDescending(r => r.Count).ThenByDescending(r => rows.IndexOf(r)).First().OrderBy(c => c.Box.X0).ToList();
        var cols = model.Select(c => (x0: c.Box.X0, x1: c.Box.X1)).ToList();
        foreach (var c in cells)                                          // widen columns to the cells that land in them
        {
            int k = Col(c);
            if (k >= 0 && c.Box.W < 1.6 * Math.Max(cols[k].x1 - cols[k].x0, 3 * size))
                cols[k] = (Math.Min(cols[k].x0, c.Box.X0), Math.Max(cols[k].x1, c.Box.X1));
        }
        int Col(TextLine c)
        {
            for (int k = 0; k < cols.Count; k++)                          // a cell over several columns: the first one it covers
                if (Math.Min(c.Box.X1, cols[k].x1) - Math.Max(c.Box.X0, cols[k].x0) > 0.25 * Math.Min(c.Box.W, cols[k].x1 - cols[k].x0))
                    return k;
            double cx = (c.Box.X0 + c.Box.X1) / 2;
            int best = -1;
            double bestD = double.MaxValue;
            for (int k = 0; k < cols.Count; k++)
            {
                double d = cx < cols[k].x0 ? cols[k].x0 - cx : cx > cols[k].x1 ? cx - cols[k].x1 : 0;
                if (d < bestD) { bestD = d; best = k; }
            }
            return best;
        }
        double widest = cols.Max(c => c.x1 - c.x0);
        var before = new List<string>();
        var after = new List<string>();
        double top = model.Min(c => c.Box.Y0);
        var grid = new List<string[]>();
        var rowBox = new List<R>();
        foreach (var row in rows)
        {
            var cellsInRow = new string[cols.Count];
            R box = row[0].Box;
            foreach (var c in row.OrderBy(c => c.Box.X0))
            {
                int spans = cols.Count(col => Math.Min(c.Box.X1, col.x1) - Math.Max(c.Box.X0, col.x0) > 0);
                if (cols.Count >= 3 && spans >= 3 && c.Box.W > 1.6 * widest)
                {
                    (c.Box.Y0 < top ? before : after).Add(c.Text.Trim());   // a line across the whole table: a note
                    continue;
                }
                int k = Col(c);
                cellsInRow[k] = cellsInRow[k] == null ? c.Text.Trim() : cellsInRow[k] + " " + c.Text.Trim();
                box = box.Union(c.Box);
            }
            if (cellsInRow.All(x => x == null)) continue;
            grid.Add(cellsInRow);
            rowBox.Add(box);
        }
        // a cell's second line (wrapped text) joins the cell above; rows with no shared columns merge (merged header cells)
        for (int r = 1; r < grid.Count;)
        {
            var up = grid[r - 1];
            var cur = grid[r];
            int n = cur.Count(x => x != null), nUp = up.Count(x => x != null);
            double gap = rowBox[r].Y0 - rowBox[r - 1].Y1;
            bool wrapped = gap < 0.35 * size && n < nUp && nUp >= 2 &&
                           Enumerable.Range(0, cols.Count).All(k => cur[k] == null || up[k] != null);
            bool disjoint = gap < 1.5 * size && Enumerable.Range(0, cols.Count).All(k => cur[k] == null || up[k] == null);
            if (wrapped || disjoint)
            {
                for (int k = 0; k < cols.Count; k++)
                    if (cur[k] != null) up[k] = up[k] == null ? cur[k] : up[k] + " " + cur[k];
                rowBox[r - 1] = rowBox[r - 1].Union(rowBox[r]);
                grid.RemoveAt(r);
                rowBox.RemoveAt(r);
            }
            else r++;
        }
        if (grid.Count == 0) return string.Join("\n\n", before.Concat(after));
        string Cell(string x) => (x ?? "").Replace("|", "\\|");
        var sb = new StringBuilder();
        if (before.Count > 0) sb.Append(string.Join("\n\n", before)).Append("\n\n");
        sb.Append("| ").Append(string.Join(" | ", grid[0].Select(Cell))).Append(" |\n");
        sb.Append("|").Append(string.Concat(Enumerable.Repeat(" --- |", cols.Count))).Append('\n');
        foreach (var row in grid.Skip(1))
            sb.Append("| ").Append(string.Join(" | ", row.Select(Cell))).Append(" |\n");
        if (after.Count > 0) sb.Append('\n').Append(string.Join("\n\n", after));
        return sb.ToString().TrimEnd();
    }

    /// <summary>Typed text under a rectangle, extended to whole words.</summary>
    public static string WordsIn(List<TextLine> lines, R rect, double margin = 0, int limit = 120)
    {
        var r = rect.Grow(margin);
        var parts = new List<string>();
        foreach (var ln in lines)
        {
            var cs = ln.Chars;
            int lo = -1, hi = -1;
            for (int i = 0; i < cs.Count; i++)
            {
                double cx = (cs[i].X0 + cs[i].X1) / 2, cy = (cs[i].Y0 + cs[i].Y1) / 2;
                if (cx >= r.X0 && cx <= r.X1 && cy >= r.Y0 && cy <= r.Y1)
                {
                    if (lo < 0) lo = i;
                    hi = i;
                }
            }
            if (lo < 0) continue;
            while (lo > 0 && !cs[lo - 1].IsSpace) lo--;
            while (hi < cs.Count - 1 && !cs[hi + 1].IsSpace) hi++;
            string t = ToLogical(string.Concat(cs.Skip(lo).Take(hi - lo + 1).Select(c => c.C))).Trim();
            if (t.Length > 0) parts.Add(t);
        }
        string s = string.Join(" / ", parts);
        return s.Length <= limit ? s : s.Substring(0, limit);
    }

    // ---------------- screen order -> reading order ----------------
    public static string ToLogical(string visual)
    {
        if (!Arabic.IsMatch(visual))
            return Clean(visual);
        var tokens = visual.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // an Arabic sentence starts at the right edge; otherwise go by which script has more words
        int ar = tokens.Count(t => Arabic.IsMatch(t)), la = tokens.Count(t => Latin.IsMatch(t));
        bool rightToLeft = Arabic.IsMatch(tokens[^1]) || ar >= la;

        var runs = new List<(bool arabic, List<string> toks)>();
        foreach (var t in tokens)
        {
            bool a = Arabic.IsMatch(t);
            if (runs.Count > 0 && runs[^1].arabic == a) runs[^1].toks.Add(t);
            else runs.Add((a, new List<string> { t }));
        }
        var outTokens = new List<string>();
        if (rightToLeft) runs.Reverse();
        foreach (var (arabic, toks) in runs)
        {
            if (arabic)
            {
                toks.Reverse();
                outTokens.AddRange(toks.Select(ReverseWord));
            }
            else outTokens.AddRange(rightToLeft
                ? toks.Select(t => Regex.Replace(t, @"^([%٪])(\d[\d.,]*)$", "$2$1"))     // "%10" on screen is "10%"
                : toks);
        }
        string s = string.Join(" ", outTokens).Normalize(NormalizationForm.FormKC)
            .Replace('ی', 'ي').Replace('ک', 'ك').Replace('ھ', 'ه');
        s = Regex.Replace(s, @"([A-Za-z0-9)\]])(?=[؀-ۿ])", "$1 ");
        s = Regex.Replace(s, @"([؀-ۿ])(?=[A-Za-z0-9(\[])", "$1 ");
        return Clean(s);
    }

    /// <summary>Reverses an Arabic word, keeping marks on their letters and numbers left to right.</summary>
    static string ReverseWord(string w)
    {
        var clusters = new List<string>();
        foreach (char c in w)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(c);
            if (clusters.Count > 0 && (cat == UnicodeCategory.NonSpacingMark || cat == UnicodeCategory.EnclosingMark))
                clusters[^1] += c;
            else
                clusters.Add(c.ToString());
        }
        clusters.Reverse();
        string r = string.Concat(clusters);
        return Regex.Replace(r, @"[A-Za-z0-9\u0660-\u0669.,%/:()\-]{2,}", m => new string(m.Value.Reverse().ToArray()));
    }

    static string Clean(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            if (c >= '' && c <= '') sb.Append('•');      // symbol-font bullets
            else if (c == ' ') sb.Append(' ');
            else if (c >= '\uFB00' && c <= '\uFB06') sb.Append(c.ToString().Normalize(NormalizationForm.FormKC));   // the ligatures ﬁ ﬂ ﬀ ﬃ: normal letters
            else if (c != '�' && !char.IsControl(c)) sb.Append(c);
        }
        return Regex.Replace(sb.ToString(), " {2,}", " ");
    }
}
