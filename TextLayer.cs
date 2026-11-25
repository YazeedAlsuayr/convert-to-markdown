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
    public bool IsSpace => string.IsNullOrWhiteSpace(C);
}

class TextLine
{
    public List<Ch> Chars = new();               // visual order, spaces included
    public double Base, Size;
    public R Box;
    public string Text => TextLayer.ToLogical(string.Concat(Chars.Select(c => c.C)));
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
            double size = Math.Max(l.PointSize, l.GlyphRectangle.Height);
            if (size < 0.5) size = 1;
            double x0 = l.StartBaseLine.X, x1 = l.EndBaseLine.X;
            if (x1 < x0) (x0, x1) = (x1, x0);
            if (x1 - x0 < 0.01) { x0 = l.GlyphRectangle.Left; x1 = l.GlyphRectangle.Right; }
            double b = H - l.StartBaseLine.Y;
            glyphs.Add(new Ch { C = l.Value, X0 = x0, X1 = x1, Base = b, Size = size, Y0 = b - 0.8 * size, Y1 = b + 0.2 * size });
        }

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
                // but a bullet or list number always stays with the text after it
                double jump = prev == null ? 0 : Math.Abs(g.Size - prev.Size) / Math.Max(g.Size, prev.Size);
                bool sizeJump = prev != null && !g.IsSpace && !prev.IsSpace && (jump > 0.4 || (jump > 0.25 && gap > 0.6 * g.Size));
                if (cur == null || ((gap > 1.5 * g.Size || sizeJump) && !IsListMarker(cur)))
                {
                    cur = new TextLine();
                    lines.Add(cur);
                    prev = null;
                }
                else if (prev != null && !g.IsSpace && !prev.IsSpace && gap > (IsArabic(g) && IsArabic(prev) ? 0.3 : 0.18) * g.Size)
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
            ln.Base = ln.Chars[0].Base;
            var sizes = ln.Chars.Select(c => c.Size).OrderBy(s => s).ToList();
            ln.Size = sizes[sizes.Count / 2];
        }
        lines.RemoveAll(l => l.Chars.Count == 0);
        return lines.OrderBy(l => Math.Round(l.Box.Y0 / 6)).ThenBy(l => l.Box.X0).ToList();
    }

    static bool IsArabic(Ch c) => Arabic.IsMatch(c.C);

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
    /// Lines are first grouped into text boxes (stacked lines, overlapping sideways, same font size),
    /// so boxes and columns placed side by side are not mixed line by line. Boxes whose lines sit on
    /// the same baselines (tables) are read row by row instead.
    /// </summary>
    public static string PageText(List<TextLine> lines)
    {
        var ls = lines.Where(l => l.Text.Trim().Length > 0).ToList();
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

        // tables: boxes side by side whose lines share baselines are read row by row
        var parent = Enumerable.Range(0, blocks.Count).ToArray();
        int Find(int a) { while (parent[a] != a) a = parent[a] = parent[parent[a]]; return a; }
        for (int a = 0; a < blocks.Count; a++)
            for (int b = a + 1; b < blocks.Count; b++)
            {
                int aligned = blocks[a].Count(i => blocks[b].Any(j =>
                    Math.Abs(ls[i].Base - ls[j].Base) < 0.3 * Math.Min(ls[i].Size, ls[j].Size)));
                int smaller = Math.Min(blocks[a].Count, blocks[b].Count);
                if (smaller >= 2 && aligned >= Math.Max(2, 0.5 * smaller)) parent[Find(a)] = Find(b);
            }
        // a one-line label on the same baseline right next to a table row (a row number, a header cell) joins that table
        for (int a = 0; a < blocks.Count; a++)
        {
            if (blocks[a].Count != 1) continue;
            var one = ls[blocks[a][0]];
            for (int b = 0; b < blocks.Count; b++)
            {
                if (b == a || Find(b) == Find(a)) continue;
                bool inTable = blocks[b].Count >= 2 || Enumerable.Range(0, blocks.Count).Any(c => c != b && Find(c) == Find(b));
                if (!inTable) continue;
                bool next = blocks[b].Any(j =>
                    Math.Abs(one.Base - ls[j].Base) < 0.3 * Math.Min(one.Size, ls[j].Size) &&
                    Math.Max(ls[j].Box.X0 - one.Box.X1, one.Box.X0 - ls[j].Box.X1) < 4 * Math.Min(one.Size, ls[j].Size));
                if (next) { parent[Find(a)] = Find(b); break; }
            }
        }
        var groups = Enumerable.Range(0, blocks.Count).GroupBy(Find)
            .Select(g => g.SelectMany(b => blocks[b]).OrderBy(i => i).ToList())
            .OrderBy(g => Math.Round(ls[g[0]].Box.Y0 / 12)).ThenBy(g => ls[g[0]].Box.X0)
            .ToList();

        var sb = new StringBuilder();
        foreach (var g in groups)
        {
            if (sb.Length > 0) sb.Append("\n\n");
            TextLine prev = null;
            foreach (int i in g)
            {
                var ln = ls[i];
                if (prev != null)
                    sb.Append(ln.Box.Y0 - prev.Box.Y1 > 0.9 * Math.Min(ln.Size, prev.Size) ? "\n\n" : "\n");
                sb.Append(ln.Text.Trim());
                prev = ln;
            }
        }
        return sb.ToString().Trim();
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
            else outTokens.AddRange(toks);
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
            else if (c != '�' && !char.IsControl(c)) sb.Append(c);
        }
        return Regex.Replace(sb.ToString(), " {2,}", " ");
    }
}
