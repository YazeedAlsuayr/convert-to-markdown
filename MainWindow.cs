// The progress window: compact like the Run box (Win + R), see-through acrylic backdrop,
// a more frosted title area, the green rectangular bar of the Windows file-copy dialog,
// and a text-only finish screen with just "Close".

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;

namespace ConvertToMarkdown;

class MainWindow : Window
{
    // ---- colours (Windows 11 Fluent values) ----
    readonly bool dark = IsDarkMode();
    Brush Fg, Sub, Err, Head, Line, Track, TrackEdge, Bar, BtnBg, BtnHover, BtnPress, BtnEdge, Solid;

    // ---- parts that change ----
    readonly Image bigLogo = new();
    readonly TextBlock title = new(), count = new(), file = new(), detail = new(), eta = new();
    readonly Border track = new();
    readonly System.Windows.Shapes.Rectangle fill = new();
    readonly StackPanel buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
    double barValue;

    // ---- work ----
    CancellationTokenSource cts = new();
    bool running, finished;
    int index, total = 1;
    double frac;
    DateTime started;
    readonly DispatcherTimer ticker = new() { Interval = TimeSpan.FromMilliseconds(250) };
    DispatcherTimer countdown;

    static readonly FontFamily Ui = new("Segoe UI Variable Text, Segoe UI");
    static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    public MainWindow()
    {
        MakeBrushes();
        Title = "Convert to Markdown";
        Width = 400;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Solid;
        FontFamily = Ui;
        Foreground = Fg;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        Icon = LoadLogo(32);

        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 32,
            GlassFrameThickness = new Thickness(-1),     // the whole window shows the backdrop
            ResizeBorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false
        });

        Content = Build();
        ticker.Tick += (_, _) => Tick();
        Program.FilesArrived += () => Dispatcher.BeginInvoke(new Action(OnFilesArrived));
        Loaded += (_, _) => StartWork();
        // When opened from Settings, Windows can lose the first drawing of the see-through window:
        // redraw everything once it is on screen.
        ContentRendered += (_, _) =>
        {
            Activate();                     // come to the front when started from the right-click menu
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => Repaint(this)));
        };
        Closing += (_, _) => { cts.Cancel(); Ai.Stop(); };
    }

    // ================= layout =================
    UIElement Build()
    {
        var root = new StackPanel();

        // --- title area (more frosted) ---
        var head = new Grid { Height = 32, Background = Head };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var smallLogo = new Image { Source = LoadLogo(16), Width = 16, Height = 16, Margin = new Thickness(12, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        RenderOptions.SetBitmapScalingMode(smallLogo, BitmapScalingMode.HighQuality);
        var headText = new TextBlock { Text = "Convert to Markdown", FontSize = 12, Foreground = Fg, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(headText, 1);
        var close = CloseButton();
        Grid.SetColumn(close, 2);
        head.Children.Add(smallLogo);
        head.Children.Add(headText);
        head.Children.Add(close);
        root.Children.Add(head);

        // --- body ---
        var body = new StackPanel { Margin = new Thickness(16, 16, 16, 16) };
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bigLogo.Source = LoadLogo(32);
        bigLogo.Width = bigLogo.Height = 32;
        bigLogo.Margin = new Thickness(0, 0, 12, 0);
        bigLogo.VerticalAlignment = VerticalAlignment.Top;
        RenderOptions.SetBitmapScalingMode(bigLogo, BitmapScalingMode.HighQuality);
        row.Children.Add(bigLogo);

        var texts = new StackPanel();
        Grid.SetColumn(texts, 1);
        var top = new Grid();
        title.Text = "Converting to Markdown";
        title.FontSize = 13.5;
        title.FontWeight = FontWeights.SemiBold;
        count.FontSize = 12;
        count.Foreground = Sub;
        count.HorizontalAlignment = HorizontalAlignment.Right;
        count.VerticalAlignment = VerticalAlignment.Center;
        top.Children.Add(title);
        top.Children.Add(count);
        texts.Children.Add(top);
        file.Text = "Starting…";
        file.FontSize = 12;
        file.Margin = new Thickness(0, 2, 0, 0);
        file.TextTrimming = TextTrimming.CharacterEllipsis;
        detail.FontSize = 12;
        detail.Foreground = Sub;
        detail.TextWrapping = TextWrapping.Wrap;
        texts.Children.Add(file);
        texts.Children.Add(detail);
        row.Children.Add(texts);
        body.Children.Add(row);

        // green rectangular bar, like the file-copy dialog
        fill.Fill = Bar;
        fill.HorizontalAlignment = HorizontalAlignment.Left;
        fill.Width = 0;
        track.Height = 10;
        track.Margin = new Thickness(0, 12, 0, 0);
        track.Background = Track;
        track.BorderBrush = TrackEdge;
        track.BorderThickness = new Thickness(1);
        track.Child = fill;
        track.SizeChanged += (_, _) => SetBar(barValue);
        body.Children.Add(track);
        root.Children.Add(body);

        // separator and footer, like the Run box
        root.Children.Add(new Border { Height = 1, Background = Line });
        var foot = new Grid { Margin = new Thickness(16, 12, 16, 12), ColumnDefinitions = { new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }, new ColumnDefinition { Width = GridLength.Auto } } };
        eta.TextWrapping = TextWrapping.Wrap;
        eta.Margin = new Thickness(0, 0, 12, 0);
        Grid.SetColumn(buttons, 1);
        eta.FontSize = 12;
        eta.Foreground = Sub;
        eta.VerticalAlignment = VerticalAlignment.Center;
        foot.Children.Add(eta);
        foot.Children.Add(buttons);
        root.Children.Add(foot);
        buttons.Children.Add(MakeButton("Cancel", Cancel));
        return root;
    }

    UIElement CloseButton()
    {
        var glyph = new TextBlock
        {
            Text = "", FontFamily = Icons, FontSize = 10, Foreground = Fg,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
        var b = new Border { Width = 46, Height = 32, Background = Brushes.Transparent, Child = glyph };
        WindowChrome.SetIsHitTestVisibleInChrome(b, true);
        b.MouseEnter += (_, _) => { b.Background = new SolidColorBrush(Color.FromRgb(0xC4, 0x2B, 0x1C)); glyph.Foreground = Brushes.White; };
        b.MouseLeave += (_, _) => { b.Background = Brushes.Transparent; glyph.Foreground = Fg; };
        b.MouseLeftButtonUp += (_, _) => { cts.Cancel(); Close(); };
        return b;
    }

    Border MakeButton(string text, Action click)
    {
        var t = new TextBlock { Text = text, FontSize = 12, Foreground = Fg, HorizontalAlignment = HorizontalAlignment.Center };
        var b = new Border
        {
            CornerRadius = new CornerRadius(4), Background = BtnBg, BorderBrush = BtnEdge, BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 5, 12, 6), MinWidth = 80, Margin = new Thickness(8, 0, 0, 0), Child = t
        };
        b.MouseEnter += (_, _) => b.Background = BtnHover;
        b.MouseLeave += (_, _) => b.Background = BtnBg;
        b.MouseLeftButtonDown += (_, e) => { b.Background = BtnPress; b.CaptureMouse(); e.Handled = true; };
        b.MouseLeftButtonUp += (_, e) =>
        {
            var p = e.GetPosition(b);
            bool inside = p.X >= 0 && p.Y >= 0 && p.X <= b.ActualWidth && p.Y <= b.ActualHeight;
            b.ReleaseMouseCapture();
            b.Background = inside ? BtnHover : BtnBg;
            if (inside) click();
        };
        return b;
    }

    static void Repaint(DependencyObject d)
    {
        if (d is UIElement u) u.InvalidateVisual();
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) Repaint(VisualTreeHelper.GetChild(d, i));
    }

    void SetBar(double v)
    {
        barValue = Math.Clamp(v, 0, 1);
        fill.Width = Math.Max(0, (track.ActualWidth - 2) * barValue);
    }

    // ================= work =================
    void OnFilesArrived()
    {
        if (finished && !running)
            StartWork();                    // new files after the last batch finished
        else
            UpdateCount();
    }

    void UpdateCount()
    {
        total = index + (running ? 1 : 0) + Program.Pending.Count;
        count.Text = total > 1 ? $"File {Math.Min(index + 1, total)} of {total}" : "";
    }

    async void StartWork()
    {
        if (running) return;
        running = true;
        finished = false;
        countdown?.Stop();
        cts = new CancellationTokenSource();
        index = 0;
        frac = 0;
        started = DateTime.Now;
        bigLogo.Visibility = Visibility.Visible;
        title.Text = "Converting to Markdown";
        detail.Foreground = Sub;
        detail.Text = "";
        eta.Text = "";
        buttons.Children.Clear();
        buttons.Children.Add(MakeButton("Cancel", Cancel));
        ticker.Start();

        var done = new List<string>();
        var failed = new List<(string name, string why)>();
        bool cancelled = false;
        var progress = new Progress<Step>(s =>
        {
            if (!string.IsNullOrEmpty(s.Detail)) detail.Text = s.Detail;
            frac = Math.Clamp(s.Frac, 0, 1);
        });

        while (true)
        {
            if (!Program.Pending.TryDequeue(out var path))
            {
                await Task.Delay(300);       // files selected together can arrive a moment apart
                if (!Program.Pending.TryDequeue(out path)) break;
            }
            frac = 0;
            UpdateCount();
            bool setup = path.StartsWith(Program.SetupAiJob);
            var setupModel = setup ? Ai.Find(path.Substring(Program.SetupAiJob.Length).Trim(':')) ?? Ai.Current : null;
            if (setup) title.Text = "Setting up the AI";
            file.Text = setup ? $"{setupModel.Name} for Convert to Markdown  ·  {setupModel.Size}, one time" : Path.GetFileName(path);
            detail.Text = "";
            var token = cts.Token;
            try
            {
                Log.Write("start " + path);
                string output = setup
                    ? await Task.Run(() => Ai.SetupAsync(setupModel, progress, token))
                    : await Task.Run(() => Converter.ConvertAsync(path, progress, token));
                done.Add(output);
                Log.Write("saved " + output);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                break;
            }
            catch (Exception e)
            {
                failed.Add((Path.GetFileName(path), e.Message));
                Log.Write("failed " + path + ": " + e);
            }
            index++;
        }
        running = false;
        Finish(done, failed, cancelled);
    }

    void Tick()
    {
        if (finished) return;
        double overall = (index + frac) / Math.Max(total, 1);
        SetBar(overall);
        double spent = (DateTime.Now - started).TotalSeconds;
        // the speed is measured from when real work starts (starting the AI and opening the file come first and
        // would make the first guesses far too long)
        if (workStart < 0 && overall > 0.005) { workStart = spent; workStartDone = overall; }
        double doneSince = overall - workStartDone, timeSince = spent - workStart;
        if (workStart >= 0 && !(doneSince > 0.1 || (doneSince > 0.03 && timeSince > 15)))
            eta.Text = "Calculating time left…";             // too early: one slow picture would give a wild guess
        else if (workStart >= 0)
        {
            // time left = speed so far × part still to do; smoothed so the number doesn't jump up and down
            double left = timeSince * (1 - overall) / doneSince;
            smoothLeft = smoothLeft < 0 ? left : 0.85 * smoothLeft + 0.15 * left;
            double s = Math.Max(0, smoothLeft - 0.25);       // this tick is a quarter second later
            smoothLeft = s;
            eta.Text = s >= 90 ? $"About {Math.Max(2, (int)Math.Round(s / 60))} min left"
                     : s >= 50 ? "About 1 min left"
                     : s >= 8 ? $"About {(int)Math.Round(s / 5) * 5} seconds left"
                     : "Almost done";
            if (spent - lastEtaLog >= 10) { lastEtaLog = spent; Log.Write($"time left shown: {eta.Text} (guess {s:0} s)"); }
        }
    }

    double lastEtaLog, workStart = -1, workStartDone;

    double smoothLeft = -1;

    void Finish(List<string> done, List<(string name, string why)> failed, bool cancelled)
    {
        finished = true;
        ticker.Stop();
        Ai.Stop();                                           // the AI only runs while converting
        if (cancelled)
        {
            Close();
            return;
        }
        SetBar(1);
        count.Text = "";
        eta.Text = "";
        bigLogo.Visibility = Visibility.Collapsed;          // text only, like Windows
        title.Text = failed.Count > 0 && done.Count == 0 ? "Couldn't convert"
                   : failed.Count > 0 ? "Completed with errors"
                   : "Conversion complete";
        if (done.Count == 1 && done[0] == "AI")
        {
            title.Text = failed.Count > 0 ? title.Text : "The AI is ready";
            file.Text = $"Pictures, handwriting and page layouts will now be read by {Ai.ModelName}.";
        }
        else if (done.Count == 1)
        {
            string dir = Path.GetDirectoryName(done[0]);
            string folder = Path.GetFileName(dir);
            file.Text = $"{Path.GetFileName(done[0])} was saved to {(string.IsNullOrEmpty(folder) ? dir : folder)}";
        }
        else
            file.Text = done.Count > 1 ? $"{done.Count} Markdown files were saved" : "No file was saved";

        detail.Text = string.Join("\n", failed.Take(4).Select(f => $"{f.name}: {Short(f.why, 140)}"));
        detail.Foreground = failed.Count > 0 ? Err : Sub;
        buttons.Children.Clear();
        buttons.Children.Add(MakeButton("Close", Close));

        if (done.Count > 0 && failed.Count == 0)
        {
            // like Windows file copy: show "complete" for a moment, then close by itself
            countdown = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            countdown.Tick += (_, _) => { countdown.Stop(); Close(); };
            countdown.Start();
        }
    }

    void Cancel()
    {
        cts.Cancel();
        buttons.Children.Clear();
        detail.Text = "Stopping after the current step…";
    }

    static string Short(string s, int n) => s.Length <= n ? s : s.Substring(0, n - 1) + "…";

    // ================= Windows 11 look =================
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            // only our own close button: no system menu, minimize or maximize buttons
            long style = GetWindowLongPtr(hwnd, -16).ToInt64();
            style &= ~(0x00080000L | 0x00020000L | 0x00010000L);
            SetWindowLongPtr(hwnd, -16, new IntPtr(style));
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, 0x0027);   // NOMOVE|NOSIZE|NOZORDER|FRAMECHANGED

            int v = dark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, 20, ref v, 4);              // dark frame
            v = 2;
            DwmSetWindowAttribute(hwnd, 33, ref v, 4);              // rounded corners
            bool glass = false;
            if (Environment.OSVersion.Version.Build >= 22621)
            {
                v = 3;                                              // 3 = acrylic backdrop
                glass = DwmSetWindowAttribute(hwnd, 38, ref v, 4) == 0;
            }
            if (glass)
            {
                HwndSource.FromHwnd(hwnd).CompositionTarget.BackgroundColor = Colors.Transparent;
                Background = Brushes.Transparent;
            }
        }
        catch (Exception ex)
        {
            Log.Write("window effects not applied: " + ex.Message);
        }
    }

    internal static bool IsDarkMode()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("AppsUseLightTheme") is int i && i == 0;
        }
        catch { return false; }
    }

    static SolidColorBrush C(byte a, byte r, byte g, byte b) => new(Color.FromArgb(a, r, g, b));

    void MakeBrushes()
    {
        if (dark)
        {
            Fg = C(255, 255, 255, 255); Sub = C(255, 0xC5, 0xC5, 0xC5); Err = C(255, 0xFF, 0x99, 0xA4);
            Head = C(0x1F, 255, 255, 255);               // more frosted title area
            Line = C(0x26, 255, 255, 255);
            Track = C(0x66, 0, 0, 0); TrackEdge = C(0x40, 255, 255, 255);
            BtnBg = C(0x0F, 255, 255, 255); BtnHover = C(0x15, 255, 255, 255); BtnPress = C(0x08, 255, 255, 255);
            BtnEdge = C(0x18, 255, 255, 255);
            Solid = C(255, 0x20, 0x20, 0x20);
        }
        else
        {
            Fg = C(255, 0x1B, 0x1B, 0x1B); Sub = C(255, 0x5C, 0x5C, 0x5C); Err = C(255, 0xC4, 0x2B, 0x1C);
            Head = C(0x99, 255, 255, 255);
            Line = C(0x1A, 0, 0, 0);
            Track = C(0x1A, 0, 0, 0); TrackEdge = C(0x45, 0, 0, 0);
            BtnBg = C(0xB3, 255, 255, 255); BtnHover = C(0x80, 0xF9, 0xF9, 0xF9); BtnPress = C(0x4D, 0xF9, 0xF9, 0xF9);
            BtnEdge = C(0x29, 0, 0, 0);
            Solid = C(255, 0xF3, 0xF3, 0xF3);
        }
        Bar = C(255, 0x06, 0xB0, 0x25);                  // Windows file-copy green
    }

    internal static ImageSource LoadLogo(int size)
    {
        try
        {
            using var s = typeof(MainWindow).Assembly.GetManifestResourceStream("markdown.ico");
            var dec = new IconBitmapDecoder(s, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frames = dec.Frames.OrderBy(f => f.PixelWidth).ToList();
            return frames.FirstOrDefault(f => f.PixelWidth >= size * 2) ?? frames.Last();   // sharp on scaled screens
        }
        catch { return null; }
    }
}
