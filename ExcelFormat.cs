// Excel number formats -> the text Excel shows in the cell ($4.50, 12.5%, 3 Nov 2026, 08:30, (350), 2.50E-05 ...).
// Covers the formats people use; anything unusual falls back to Excel's "General" look.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ConvertToMarkdown;

static class ExcelFormat
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Excel's built-in formats (ids without a formatCode in styles.xml). Dates use an unambiguous day-month-year look.</summary>
    public static readonly Dictionary<int, string> BuiltIn = new()
    {
        [0] = "General", [1] = "0", [2] = "0.00", [3] = "#,##0", [4] = "#,##0.00", [9] = "0%", [10] = "0.00%", [11] = "0.00E+00",
        [12] = "General", [13] = "General", [14] = "d mmm yyyy", [15] = "d-mmm-yy", [16] = "d-mmm", [17] = "mmm-yy",
        [18] = "h:mm AM/PM", [19] = "h:mm:ss AM/PM", [20] = "h:mm", [21] = "h:mm:ss", [22] = "d mmm yyyy h:mm",
        [37] = "#,##0;(#,##0)", [38] = "#,##0;(#,##0)", [39] = "#,##0.00;(#,##0.00)", [40] = "#,##0.00;(#,##0.00)",
        [45] = "mm:ss", [46] = "[h]:mm:ss", [47] = "mm:ss.0", [48] = "##0.0E+0", [49] = "@"
    };

    static readonly string[] MonthShort = { "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec" };
    static readonly string[] MonthLong = { "January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December" };
    static readonly string[] DayShort = { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };
    static readonly string[] DayLong = { "Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday" };

    /// <summary>A number as Excel's General format shows it: 0.30000000000000004 -> 0.3, 0.000025 -> 2.5E-05.</summary>
    public static string General(double v)
    {
        if (double.IsNaN(v) || double.IsInfinity(v)) return "#NUM!";
        if (v == 0) return "0";
        double a = Math.Abs(v);
        if (a >= 1e11 || a < 1e-9) return v.ToString("0.#####E+00", Inv);
        double r = double.Parse(v.ToString("G15", Inv), Inv);
        return r.ToString("G11", Inv);
    }

    public static string Format(double v, string code, bool date1904)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Trim().Equals("General", StringComparison.OrdinalIgnoreCase)) return General(v);
        try
        {
            var secs = Sections(code);
            string sec;
            bool negSection = false;
            if (secs.Count == 1 || v > 0 || (v == 0 && secs.Count < 3)) sec = secs[0];
            else if (v < 0) { sec = secs[1]; negSection = true; }
            else sec = secs[2];
            if (sec.Length == 0) return "";
            if (sec.Contains('@')) return General(v);                                // a text format on a number
            if (sec.Trim().Equals("General", StringComparison.OrdinalIgnoreCase)) return (negSection ? "-" : "") + General(negSection ? -v : v);
            if (sec.Contains('?') && sec.Contains('/')) return General(v);              // fractions: shown as a plain number
            var toks = Tokens(StripBrackets(sec));
            if (IsDate(toks)) return FormatDate(v, toks, date1904);
            string s = FormatNumber(Math.Abs(v), toks);
            if (v < 0 && !negSection) s = "-" + s;                                      // one section: Excel adds the minus sign
            return s;
        }
        catch { return General(v); }
    }

    static List<string> Sections(string code)
    {
        var outp = new List<string>();
        var cur = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < code.Length; i++)
        {
            char ch = code[i];
            if (ch == '"') { quoted = !quoted; cur.Append(ch); }
            else if (ch == '\\' && i + 1 < code.Length) { cur.Append(ch).Append(code[i + 1]); i++; }
            else if (ch == ';' && !quoted) { outp.Add(cur.ToString()); cur.Clear(); }
            else cur.Append(ch);
        }
        outp.Add(cur.ToString());
        return outp;
    }

    /// <summary>[Red], [>=100], [$-409] are dropped; [$€-407] becomes the currency sign; [h] [mm] [ss] stay.</summary>
    static string StripBrackets(string sec) => Regex.Replace(sec, @"\[([^\]]*)\]", m =>
    {
        string t = m.Groups[1].Value;
        if (Regex.IsMatch(t, @"^(h+|m+|s+)$", RegexOptions.IgnoreCase)) return m.Value;
        if (t.StartsWith("$"))
        {
            string sym = t.Substring(1).Split('-')[0];
            return sym.Length > 0 ? "\"" + sym + "\"" : "";
        }
        return "";
    });

    /// <summary>The format as pieces: literal text (quoted, escaped, padding) or format characters.</summary>
    static List<(bool lit, string text)> Tokens(string sec)
    {
        var outp = new List<(bool, string)>();
        for (int i = 0; i < sec.Length; i++)
        {
            char ch = sec[i];
            if (ch == '"')
            {
                int j = sec.IndexOf('"', i + 1);
                if (j < 0) j = sec.Length;
                outp.Add((true, sec.Substring(i + 1, j - i - 1)));
                i = j;
            }
            else if (ch == '\\' && i + 1 < sec.Length) { outp.Add((true, sec[i + 1].ToString())); i++; }
            else if (ch == '_' && i + 1 < sec.Length) { outp.Add((true, " ")); i++; }   // padding the width of a character
            else if (ch == '*' && i + 1 < sec.Length) i++;                              // repeat-to-fill
            else outp.Add((false, ch.ToString()));
        }
        return outp;
    }

    static bool IsDate(List<(bool lit, string text)> toks)
    {
        string code = string.Concat(toks.Where(t => !t.lit).Select(t => t.text));
        code = Regex.Replace(code, @"AM/PM|A/P", "", RegexOptions.IgnoreCase);
        return Regex.IsMatch(code, "[dmyhsDMYHS]");
    }

    static string FormatDate(double v, List<(bool lit, string text)> toks, bool date1904)
    {
        var dt = (date1904 ? new DateTime(1904, 1, 1) : new DateTime(1899, 12, 30)).AddMilliseconds(Math.Round(v * 86400000.0));
        dt = new DateTime(dt.Year, dt.Month, dt.Day, dt.Hour, dt.Minute, dt.Second).AddSeconds(dt.Millisecond >= 500 ? 1 : 0);
        // rebuild the code with a marker for every literal, then split it into parts
        var lits = new Queue<string>(toks.Where(t => t.lit).Select(t => t.text));
        string code = string.Concat(toks.Select(t => t.lit ? "\0" : t.text));
        bool ampm = Regex.IsMatch(code, @"AM/PM|A/P", RegexOptions.IgnoreCase);
        var parts = new List<(string kind, string text)>();
        for (int i = 0; i < code.Length;)
        {
            char c = code[i];
            if (c == '\0') { parts.Add(("lit", lits.Count > 0 ? lits.Dequeue() : "")); i++; continue; }
            var m = Regex.Match(code.Substring(i), @"^(AM/PM|A/P)", RegexOptions.IgnoreCase);
            if (m.Success) { parts.Add(("ampm", m.Value)); i += m.Length; continue; }
            m = Regex.Match(code.Substring(i), @"^\[(h+|m+|s+)\]", RegexOptions.IgnoreCase);
            if (m.Success) { parts.Add(("elapsed", m.Groups[1].Value.ToLowerInvariant())); i += m.Length; continue; }
            char lc = char.ToLowerInvariant(c);
            if ("dmyhs".IndexOf(lc) >= 0)
            {
                int j = i;
                while (j < code.Length && char.ToLowerInvariant(code[j]) == lc) j++;
                parts.Add((lc.ToString(), code.Substring(i, j - i)));
                i = j;
                continue;
            }
            if (c == '.' && i + 1 < code.Length && code[i + 1] == '0')
            {
                int j = i + 1;
                while (j < code.Length && code[j] == '0') j++;
                parts.Add(("frac", code.Substring(i, j - i)));
                i = j;
                continue;
            }
            parts.Add(("lit", c.ToString()));
            i++;
        }
        // "m" right after hours or right before seconds means minutes
        for (int k = 0; k < parts.Count; k++)
        {
            if (parts[k].kind != "m") continue;
            var before = parts.Take(k).LastOrDefault(p => "dmyhs".Contains(p.kind) && p.kind.Length == 1 || p.kind == "elapsed");
            var after = parts.Skip(k + 1).FirstOrDefault(p => "dmyhs".Contains(p.kind) && p.kind.Length == 1 || p.kind == "elapsed");
            if (before.kind == "h" || before.kind == "elapsed" || after.kind == "s") parts[k] = ("min", parts[k].text);
        }
        var sb = new StringBuilder();
        foreach (var (kind, text) in parts)
        {
            int n = text.Length;
            switch (kind)
            {
                case "lit": sb.Append(text); break;
                case "y": sb.Append(n > 2 ? dt.Year.ToString("0000") : (dt.Year % 100).ToString("00")); break;
                case "m":
                    sb.Append(n == 1 ? dt.Month.ToString() : n == 2 ? dt.Month.ToString("00") : n == 3 ? MonthShort[dt.Month - 1]
                            : n == 4 ? MonthLong[dt.Month - 1] : MonthShort[dt.Month - 1].Substring(0, 1));
                    break;
                case "d":
                    sb.Append(n == 1 ? dt.Day.ToString() : n == 2 ? dt.Day.ToString("00") : n == 3 ? DayShort[(int)dt.DayOfWeek] : DayLong[(int)dt.DayOfWeek]);
                    break;
                case "h":
                    int h = ampm ? (dt.Hour % 12 == 0 ? 12 : dt.Hour % 12) : dt.Hour;
                    sb.Append(n > 1 ? h.ToString("00") : h.ToString());
                    break;
                case "min": sb.Append(n > 1 ? dt.Minute.ToString("00") : dt.Minute.ToString()); break;
                case "s": sb.Append(n > 1 ? dt.Second.ToString("00") : dt.Second.ToString()); break;
                case "elapsed":
                    double total = text[0] == 'h' ? v * 24 : text[0] == 'm' ? v * 1440 : v * 86400;
                    sb.Append(((long)Math.Floor(total + 1e-9)).ToString(Inv));
                    break;
                case "ampm": sb.Append(text.Length > 3 ? (dt.Hour < 12 ? "AM" : "PM") : (dt.Hour < 12 ? "A" : "P")); break;
            }
        }
        return sb.ToString();
    }

    static string FormatNumber(double v, List<(bool lit, string text)> toks)
    {
        var lits = toks.Where(t => t.lit).Select(t => t.text).ToList();
        string code = string.Concat(toks.Select(t => t.lit ? "\0" : t.text));
        var m = Regex.Match(code, @"[0#?](?:[0#?,.]|[Ee][+-]?)*");
        if (!m.Success) return Literal(code, lits, 0, code.Length, 0);
        int s = m.Index, e = m.Index + m.Length;
        if (s > 0 && code[s - 1] == '.') s--;
        string num = code.Substring(s, e - s);
        int pct = code.Count(c => c == '%');
        v *= Math.Pow(100, pct);

        string body;
        var sci = Regex.Match(num, @"[Ee]([+-]?)");
        if (sci.Success)
        {
            string mant = num.Substring(0, sci.Index), expDigits = num.Substring(sci.Index + sci.Length);
            int dec = mant.Contains('.') ? mant.Split('.')[1].Length : 0;
            int intDigits = Math.Max(mant.Split('.')[0].Replace(",", "").Length, 1);
            int ex = 0;
            double mv = 0;
            if (v != 0)
            {
                ex = (int)Math.Floor(Math.Log10(v));
                if (intDigits > 1) ex -= ((ex % intDigits) + intDigits) % intDigits;   // engineering notation (##0.0E+0)
                mv = v / Math.Pow(10, ex);
                if (Math.Round(mv, dec) >= Math.Pow(10, intDigits)) { mv /= 10; ex++; }
            }
            string sign = ex < 0 ? "-" : sci.Groups[1].Value == "+" ? "+" : "";
            body = mv.ToString("F" + dec, Inv) + "E" + sign + Math.Abs(ex).ToString(new string('0', Math.Max(expDigits.Length, 1)), Inv);
        }
        else
        {
            while (num.EndsWith(",")) { v /= 1000; num = num.Substring(0, num.Length - 1); }   // 0.0, = thousands
            num = Regex.Replace(num, @",+\.", ".");
            bool thousands = num.Contains(',');
            string ip = num, fp = "";
            if (num.Contains('.')) { ip = num.Split('.')[0]; fp = num.Substring(num.IndexOf('.') + 1); }
            int dec = fp.Count(c => c == '0' || c == '#' || c == '?');
            int minDec = fp.Count(c => c == '0');
            double r = Math.Round(v, dec, MidpointRounding.AwayFromZero);
            body = r.ToString((thousands ? "N" : "F") + dec, Inv);
            if (dec > minDec && body.Contains('.'))
            {
                string i0 = body.Split('.')[0], f0 = body.Split('.')[1].TrimEnd('0');
                if (f0.Length < minDec) f0 = f0.PadRight(minDec, '0');
                body = f0.Length > 0 ? i0 + "." + f0 : i0 + (fp.Length > 0 && minDec == 0 && fp.All(c => c == '#') ? "." : "");
                if (body.EndsWith(".")) body = body.TrimEnd('.');
            }
            if (!ip.Contains('0') && body.StartsWith("0")) body = body.Substring(1);   // #.00 -> .50
        }
        return Literal(code, lits, 0, s, 0) + body + Literal(code, lits, e, code.Length, code.Substring(0, e).Count(c => c == '\0'));
    }

    static string Literal(string code, List<string> lits, int from, int to, int litIndex)
    {
        var sb = new StringBuilder();
        for (int i = from; i < to; i++)
        {
            if (code[i] == '\0') { if (litIndex < lits.Count) sb.Append(lits[litIndex]); litIndex++; }
            else sb.Append(code[i]);
        }
        return sb.ToString();
    }
}
