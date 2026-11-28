// "Set up for this PC": a quick look at the PC, a short AI speed test with built-in samples, and the choice
// Faster / More accurate with time estimates (the recommended one is marked). It is a page of the Settings
// window (same look), shown once after the AI is set up and reopenable from Settings any time.

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace ConvertToMarkdown;

/// <summary>What this PC has, and how fast the AI is on it.</summary>
static class PcCheck
{
    public record Info(string Gpu, double GpuGb, double RamGb, string Cpu, int Cores, double FreeGb);

    /// <summary>The AI's speed in seconds: one picture, one group of handwriting notes, and for "Describe layout and
    /// pictures": one page layout and one picture described.</summary>
    public record Speed(string Device, double PictureSec, double NoteSec, double LayoutSec, double PictureDescribeSec, bool Readable, DateTime When);

    // ---------------- the PC ----------------
    public static Info Look()
    {
        var (gpu, gb) = Graphics();
        double free = 0;
        try { free = new DriveInfo(Path.GetPathRoot(Program.DataDir)).AvailableFreeSpace / 1073741824.0; } catch { }
        string cpu = "";
        try { cpu = (Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", "") as string ?? "").Trim(); } catch { }
        return new Info(gpu, gb, Ram(), cpu, Environment.ProcessorCount, free);
    }

    /// <summary>The graphics card with the most memory (Windows keeps the list in the registry).</summary>
    static (string name, double gb) Graphics()
    {
        string best = null;
        double bestGb = 0;
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            foreach (var sub in cls?.GetSubKeyNames() ?? Array.Empty<string>())
            {
                if (!sub.All(char.IsDigit)) continue;
                try
                {
                    using var k = cls.OpenSubKey(sub);
                    if (k?.GetValue("DriverDesc") is not string name) continue;
                    if (name.Contains("Basic Display") || name.Contains("Remote") || name.Contains("Virtual")) continue;
                    double gb = k.GetValue("HardwareInformation.qwMemorySize") switch
                    {
                        long l => l / 1073741824.0,
                        byte[] b when b.Length >= 8 => BitConverter.ToInt64(b, 0) / 1073741824.0,
                        _ => k.GetValue("HardwareInformation.MemorySize") switch
                        {
                            int i => (uint)i / 1073741824.0,
                            byte[] b when b.Length >= 4 => BitConverter.ToUInt32(b, 0) / 1073741824.0,
                            _ => 0
                        }
                    };
                    if (best == null || gb > bestGb) { best = name; bestGb = gb; }
                }
                catch { }
            }
        }
        catch { }
        return (best ?? "", bestGb);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MemoryStatus
    {
        public uint Length, Load;
        public ulong TotalPhys, AvailPhys, TotalPage, AvailPage, TotalVirtual, AvailVirtual, AvailExtended;
    }
    [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MemoryStatus m);

    static double Ram()
    {
        var m = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        return GlobalMemoryStatusEx(ref m) ? m.TotalPhys / 1073741824.0 : 0;
    }

    // ---------------- built-in samples (drawn by the app, nothing is downloaded) ----------------
    /// <summary>A slide-like picture with a title and a few lines with numbers. Each variant is a little different,
    /// so the AI can't reuse its earlier work and the time is real.</summary>
    public static byte[] SamplePicture(int variant) => Render(900, 540, dc =>
    {
        string[] titles = { "Quarterly sales review", "Monthly team update", "Project plan", "Store opening checklist" };
        string[][] all =
        {
            new[] { "• Revenue grew 12% to $4.8 million", "• Two new stores opened in March", "• Online orders: 18,450 (up from 15,200)",
                    "• Returns fell to 2.1% of sales", "• Next step: hire 3 more staff by June" },
            new[] { "• Customer calls answered: 18,450", "• Average waiting time: 2 min 40 s", "• New office opens on 14 May",
                    "• Training budget: $12,300 (up 8%)", "• Next meeting: Tuesday at 10:30" },
            new[] { "• Phase 1: design (6 weeks)", "• Phase 2: build and test (10 weeks)", "• Budget: $48,000",
                    "• Team: 5 people, 2 part-time", "• Launch: 1 September" },
            new[] { "• Signs and lights installed", "• Stock delivered: 1,240 items", "• Staff trained: 9 of 12",
                    "• Opening hours: 9:00 to 21:00", "• Grand opening: Friday 18:00" }
        };
        Write(dc, titles[variant % 4], 50, 40, 40, "Segoe UI", true);
        var lines = all[variant % 4];
        for (int i = 0; i < lines.Length; i++) Write(dc, lines[i], 70, 140 + 70 * i, 30, "Segoe UI");
    });

    /// <summary>A whole page (title, paragraphs, a picture box), as the layout description sees one.</summary>
    public static byte[] SamplePage() => Render(594, 768, dc =>
    {
        Write(dc, "Annual report 2026", 40, 36, 26, "Segoe UI", true);
        string[] para =
        {
            "This report sums up the year: sales, staff and the plans",
            "for next year. Sales grew in every region, and two new",
            "stores opened. Costs stayed close to the budget."
        };
        for (int i = 0; i < para.Length; i++) Write(dc, para[i], 40, 92 + 22 * i, 15, "Segoe UI");
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xDD, 0xE8, 0xF5)), new Pen(Brushes.Gray, 1), new Rect(40, 180, 250, 170));
        Write(dc, "Sales by region (chart)", 60, 255, 14, "Segoe UI");
        string[] side = { "North: $1.9 million", "South: $1.4 million", "East: $0.9 million", "West: $0.6 million" };
        for (int i = 0; i < side.Length; i++) Write(dc, side[i], 310, 190 + 26 * i, 15, "Segoe UI");
        Write(dc, "Next year", 40, 380, 20, "Segoe UI", true);
        string[] more = { "• Open three more stores", "• Train all new staff in the first month", "• Keep costs within 2% of the budget" };
        for (int i = 0; i < more.Length; i++) Write(dc, more[i], 50, 420 + 24 * i, 15, "Segoe UI");
        Write(dc, "Page 4", 270, 730, 12, "Segoe UI");
    });

    /// <summary>Two short handwritten-style notes, one under the other, like the app sends for pen notes.</summary>
    public static byte[] SampleNotes(int variant)
    {
        string[][] notes =
        {
            new[] { "Call the supplier → Monday", "Check total = 1,250" },
            new[] { "Send the report to Sara", "Room 12 at 3 pm" },
            new[] { "Buy paper + ink", "Ask about the price" }
        };
        var n = notes[variant % notes.Length];
        return Render(760, 200, dc =>
        {
            Write(dc, n[0], 30, 24, 44, "Ink Free, Segoe Print");
            Write(dc, n[1], 30, 112, 44, "Ink Free, Segoe Print");
        });
    }

    static byte[] Render(int w, int h, Action<DrawingContext> draw)
    {
        var v = new DrawingVisual();
        using (var dc = v.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, h));
            draw(dc);
        }
        var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(v);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    static void Write(DrawingContext dc, string s, double x, double y, double size, string font, bool bold = false)
    {
        var ft = new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily(font), FontStyles.Normal, bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal),
            size, Brushes.Black, 1.0);
        dc.DrawText(ft, new Point(x, y));
    }

    // ---------------- the speed test ----------------
    /// <summary>
    /// Starts the AI, asks one warm-up question, then times 2 sample pictures and 2 sample handwriting groups
    /// (about 20-60 s). The pictures are made before (they need the window's thread).
    /// </summary>
    public static async Task<Speed> TestAsync(byte[][] pictures, byte[][] notes, byte[] page, IProgress<string> say, CancellationToken ct)
    {
        say.Report("Starting the AI…");
        if (!await Ai.EnsureRunningAsync(null, ct)) throw new InvalidOperationException(Ai.Status);
        say.Report("Warming up…");
        await Ai.AskAsync(Converter.NotesPrompt(2), notes[2], 120, false, ct);
        double pic = 0, note = 0;
        string answer = "";
        for (int i = 0; i < 2; i++)
        {
            say.Report($"Reading a sample picture ({i + 1} of 2)…");
            var sw = Stopwatch.StartNew();
            string a = await Ai.AskAsync(Ai.PictureTextPrompt, pictures[i], 900, false, ct);
            pic += sw.Elapsed.TotalSeconds;
            if (i == 0) answer = a;
        }
        for (int i = 0; i < 2; i++)
        {
            say.Report($"Reading sample handwriting ({i + 1} of 2)…");
            var sw = Stopwatch.StartNew();
            await Ai.AskAsync(Converter.NotesPrompt(2), notes[i], 120, false, ct);
            note += sw.Elapsed.TotalSeconds;
        }
        // "Describe layout and pictures": the page layout (same size and limit as in a PDF) and one picture described
        say.Report("Describing a sample page…");
        var clock = Stopwatch.StartNew();
        await Ai.AskAsync(Ai.DescribePrompt, page, 200, false, ct);
        double layout = clock.Elapsed.TotalSeconds;
        say.Report("Describing a sample picture…");
        clock.Restart();
        await Ai.AskAsync(Ai.PicturePrompt, pictures[2], 900, false, ct);
        double picDescribe = clock.Elapsed.TotalSeconds;

        bool readable = answer.Replace(",", "").Contains("18450") && answer.Contains("Revenue", StringComparison.OrdinalIgnoreCase);
        var s = new Speed(Ai.Device, pic / 2, note / 2, layout, picDescribe, readable, DateTime.Now);
        Log.Write($"PC test: AI on the {s.Device}, picture {s.PictureSec:0.0} s, handwriting {s.NoteSec:0.0} s, " +
                  $"page layout {s.LayoutSec:0.0} s, picture described {s.PictureDescribeSec:0.0} s, answer {(readable ? "right" : "WRONG")}");
        return s;
    }

    // ---------------- estimates for a typical file ----------------
    // A general example, the same for everyone: a 20-page PDF with 10 pictures and handwriting on a few pages
    // (about 8 handwriting questions). Time = starting the AI and reading the typed text (Fixed) + one question
    // per picture + one per handwriting group; with "Describe layout and pictures" also one layout question per
    // page, and each picture is described as well as read. (Checked against real files: see CLAUDE.md, stage 7.)
    public const int Pages = 20, Pictures = 10;
    const double NoteGroups = 8, Fixed = 11;

    public static (double fast, double accurate) Estimate(Speed s, bool describe)
    {
        double fast = Fixed + Pictures * (describe ? s.PictureDescribeSec : s.PictureSec) + (describe ? Pages * s.LayoutSec : 0);
        return (fast, fast + NoteGroups * s.NoteSec);
    }

    /// <summary>More accurate is recommended when it isn't much slower: on a graphics card, and at most
    /// 60% longer or under 2 minutes for the typical file.</summary>
    public static bool RecommendAccurate(Speed s, bool describe)
    {
        var (fast, acc) = Estimate(s, describe);
        return s.Device == "graphics card" && (acc <= 1.6 * fast || acc <= 120);
    }

    public static string Time(double sec)
    {
        if (sec < 55) return $"{Math.Max(10, Math.Round(sec / 5) * 5):0} seconds";
        if (sec >= 600) return $"{Math.Round(sec / 60):0} minutes";
        int s = (int)(Math.Round(sec / 10) * 10);
        return s % 60 == 0 ? $"{s / 60} min" : $"{s / 60} min {s % 60} s";
    }

    static string N(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);
    static double D(string v) => double.Parse(v, CultureInfo.InvariantCulture);

    public static string Save(Speed s) => string.Join("|", "v2", s.Device, N(s.PictureSec), N(s.NoteSec), N(s.LayoutSec), N(s.PictureDescribeSec),
        s.Readable ? "1" : "0", s.When.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

    /// <summary>The saved test, or null (none yet, or an older test without the Describe times: test again).</summary>
    public static Speed Load(string saved)
    {
        try
        {
            var p = saved.Split('|');
            if (p.Length < 8 || p[0] != "v2") return null;
            return new Speed(p[1], D(p[2]), D(p[3]), D(p[4]), D(p[5]), p[6] == "1",
                             DateTime.ParseExact(p[7], "yyyy-MM-dd", CultureInfo.InvariantCulture));
        }
        catch { return null; }
    }
}

/// <summary>The Speed section at the top of Settings: Faster / More accurate with times from this PC's test.</summary>
partial class SettingsWindow
{
    readonly StackPanel speedPanel = new();
    PcCheck.Info pcInfo;
    CancellationTokenSource testCts;
    bool testing, testHooked;
    TextBlock testStatus;
    string testProblem;

    /// <summary>Opened from the first-run setup or --setup-pc: Settings, with the speed test if there is none yet.</summary>
    void ShowPcPage() => ShowSettingsPage();

    /// <summary>The first time (no test yet, AI on): test straight away, once the window is open.</summary>
    void StartTestIfNeeded()
    {
        if (!Ai.Enabled || testing || PcCheck.Load(Settings.PcTest) != null || testProblem != null) return;
        if (IsLoaded) { _ = RunTestAsync(); return; }
        RoutedEventHandler start = null;
        start = (_, _) => { Loaded -= start; _ = RunTestAsync(); };     // before that, updates can't reach the window
        Loaded += start;
    }

    UIElement Section(string text) => new TextBlock { Text = text, FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 2) };

    TextBlock Para(string text, bool warn = false) => new()
    {
        Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0),
        Foreground = warn ? new SolidColorBrush(dark ? Color.FromRgb(0xFC, 0xE1, 0x00) : Color.FromRgb(0x9D, 0x5D, 0x00)) : Sub
    };

    /// <summary>A small clickable word in the accent color ("Test again", "Cancel").</summary>
    TextBlock Link(string text, Action click)
    {
        var t = new TextBlock { Text = text, FontSize = 12, Foreground = Accent, Cursor = Cursors.Hand };
        t.MouseEnter += (_, _) => t.TextDecorations = TextDecorations.Underline;
        t.MouseLeave += (_, _) => t.TextDecorations = null;
        t.MouseLeftButtonUp += (_, _) => click();
        return t;
    }

    /// <summary>Grey text followed by a link, on one line when it fits.</summary>
    UIElement LineWithLink(string text, string link, Action click)
    {
        var t = new TextBlock { FontSize = 12, Foreground = Sub, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        if (text.Length > 0) t.Inlines.Add(new System.Windows.Documents.Run(text + "  "));
        var l = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run(link)) { Foreground = Accent, TextDecorations = null, Cursor = Cursors.Hand };
        l.MouseEnter += (_, _) => l.TextDecorations = TextDecorations.Underline;
        l.MouseLeave += (_, _) => l.TextDecorations = null;
        l.Click += (_, _) => click();
        t.Inlines.Add(l);
        return t;
    }

    /// <summary>Grey text on the left, a normal button on the right ("Test again", "Test this PC", "Cancel").</summary>
    UIElement LineWithButton(string text, string label, Action click)
    {
        var row = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new TextBlock { Text = text, FontSize = 12, Foreground = Sub, TextWrapping = TextWrapping.Wrap,
                                         VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        var b = MakeButton(label, click, false);
        b.Margin = new Thickness(0);
        b.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(b, 1);
        row.Children.Add(b);
        return row;
    }

    /// <summary>"This PC: graphics card · memory · free space" for the Advanced part.</summary>
    UIElement PcInfoLine()
    {
        pcInfo ??= PcCheck.Look();
        var i = pcInfo;
        string gpu = i.Gpu.Length == 0 ? "no graphics card found" : i.GpuGb >= 0.5 ? $"{i.Gpu} ({i.GpuGb:0} GB)" : i.Gpu;
        return new TextBlock { Text = $"This PC: {gpu} · {i.RamGb:0} GB memory · {i.FreeGb:0} GB free", FontSize = 12, Foreground = Sub,
                               TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
    }

    /// <summary>Fills the Speed section for the current state: no AI, testing, no test yet, or the two choices.</summary>
    void RefreshSpeed()
    {
        speedPanel.Children.Clear();
        speedPanel.Children.Add(Section("Speed"));
        if (!Ai.Enabled)
        {
            bool installed = Ai.ServerExe != null && Ai.Models.Any(Ai.HasModel);
            speedPanel.Children.Add(Para(installed
                ? "The AI is turned off (Advanced settings), so pictures and handwriting are read by the quick built-in reader: fast, less accurate."
                : "The AI is not set up, so pictures and handwriting are read by the quick built-in reader: fast, less accurate. \"Set up AI\" downloads it once (about 3 GB)."));
            return;
        }
        if (testing)
        {
            speedPanel.Children.Add(Para("Testing how fast the AI is on this PC (about 30-90 seconds)…"));
            testStatus = Para("Starting…");
            speedPanel.Children.Add(testStatus);
            speedPanel.Children.Add(LineWithButton("", "Cancel", () => testCts?.Cancel()));
            return;
        }
        var s = PcCheck.Load(Settings.PcTest);
        if (testProblem != null) speedPanel.Children.Add(Para(testProblem, true));
        if (s == null)
        {
            speedPanel.Children.Add(LineWithButton("A short test (about 30-90 seconds) shows the times on this PC and recommends Faster or More accurate.",
                                                 "Test this PC", () => _ = RunTestAsync()));
            return;
        }

        bool describe = Settings.Describe;                     // the times follow the user's own settings
        var (fast, acc) = PcCheck.Estimate(s, describe);
        var (otherFast, otherAcc) = PcCheck.Estimate(s, !describe);
        bool recAcc = PcCheck.RecommendAccurate(s, describe);
        speedPanel.Children.Add(Choice("Faster", $"About {PcCheck.Time(fast)}*. Handwriting is read by the quick built-in reader.",
            !recAcc, () => !Settings.HandwritingAi, () => { Settings.HandwritingAi = false; Settings.PcSetupDone = true; ShowSettingsPage(); }));
        speedPanel.Children.Add(Choice("More accurate", $"About {PcCheck.Time(acc)}*. Handwriting is read by the AI.",
            recAcc, () => Settings.HandwritingAi, () => { Settings.HandwritingAi = true; Settings.PcSetupDone = true; ShowSettingsPage(); }));
        string other = $"Faster about {PcCheck.Time(otherFast)}, More accurate about {PcCheck.Time(otherAcc)}.";
        speedPanel.Children.Add(Para($"*For a typical {PcCheck.Pages}-page PDF with {PcCheck.Pictures} pictures and some handwriting" +
            (describe ? ", including \"Describe layout and pictures\" (on). Without it: " + other
                      : ". With \"Describe layout and pictures\" on: " + other)));
        if (s.Device != "graphics card")
            speedPanel.Children.Add(Para("The AI runs on the processor on this PC (the graphics card couldn't be used), so pictures are slow.", true));
        if (!s.Readable)
            speedPanel.Children.Add(Para("The AI's test answer looked wrong. If results look odd, turn off the AI in Advanced settings.", true));
        if (fast > 300)
            speedPanel.Children.Add(Para("Tip: for the fastest conversions on this PC, turn off the AI in Advanced settings (less accurate)."));
        speedPanel.Children.Add(LineWithButton($"AI: {Ai.ModelName} on the {s.Device} · tested {s.When.ToString("d MMM yyyy", CultureInfo.InvariantCulture)}.",
                                             "Test again", () => _ = RunTestAsync()));
    }

    /// <summary>A round choice (only one can be on), with "Recommended for this PC" next to the title when it is.</summary>
    UIElement Choice(string title, string explanation, bool recommended, Func<bool> isOn, Action pick)
    {
        bool on = isOn();
        var dot = new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Background = AccentFg, Visibility = on ? Visibility.Visible : Visibility.Hidden,
                               HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var ring = new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Child = dot,
                                Background = on ? Accent : Brushes.Transparent, BorderBrush = on ? Accent : Sub,
                                VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 12, 0) };
        var head = new TextBlock { FontSize = 13, Foreground = Fg };
        head.Inlines.Add(new System.Windows.Documents.Run(title));
        if (recommended) head.Inlines.Add(new System.Windows.Documents.Run("   Recommended for this PC") { Foreground = Accent, FontSize = 12, FontWeight = FontWeights.SemiBold });
        var texts = new StackPanel();
        texts.Children.Add(head);
        texts.Children.Add(new TextBlock { Text = explanation, FontSize = 12, Foreground = Sub, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
        var row = new Grid { Background = Brushes.Transparent, Margin = new Thickness(0, 6, 0, 6), Cursor = Cursors.Hand };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(texts, 1);
        row.Children.Add(ring);
        row.Children.Add(texts);
        row.MouseEnter += (_, _) => { if (!on) ring.Background = Hover; };
        row.MouseLeave += (_, _) => { if (!on) ring.Background = Brushes.Transparent; };
        row.MouseLeftButtonUp += (_, _) => { if (!on) pick(); };
        return row;
    }

    async Task RunTestAsync()
    {
        if (testing) return;
        if (!testHooked)
        {
            testHooked = true;
            Closed += (_, _) => { testCts?.Cancel(); Ai.Stop(); };
        }
        testing = true;
        testProblem = null;
        testCts = new CancellationTokenSource();
        RefreshSpeed();
        try
        {
            // the sample pictures are drawn here, on the window's thread
            var pictures = new[] { PcCheck.SamplePicture(0), PcCheck.SamplePicture(1), PcCheck.SamplePicture(2) };
            var notes = new[] { PcCheck.SampleNotes(0), PcCheck.SampleNotes(1), PcCheck.SampleNotes(2) };
            var page = PcCheck.SamplePage();
            var say = new Progress<string>(t => Dispatcher.BeginInvoke(() => { if (testStatus != null) testStatus.Text = t; }));
            var result = await PcCheck.TestAsync(pictures, notes, page, say, testCts.Token);
            Settings.PcTest = PcCheck.Save(result);
            if (!Settings.PcSetupDone)                         // first time: pick the recommended one for the user
            {
                Settings.HandwritingAi = PcCheck.RecommendAccurate(result, Settings.Describe);
                Settings.PcSetupDone = true;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            Log.Write("PC test: " + e);
            testProblem = "The speed test didn't work: " + (Ai.Status.Length > 0 ? Ai.Status : "the AI didn't answer") + ".";
        }
        finally
        {
            testing = false;
            Ai.Stop();                                         // the AI only runs while it is needed
        }
        if (IsVisible) ShowSettingsPage();                     // also updates the handwriting tick in Advanced
    }
}
