// Settings: the "Describe layout and pictures" tick, the AI on/off, and which AI model to use.
// Same Windows 11 look as the progress window. Every change is saved at once.

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;

namespace ConvertToMarkdown;

class SettingsWindow : Window
{
    readonly bool dark = MainWindow.IsDarkMode();
    Brush Fg, Sub, Head, Line, BtnBg, BtnHover, BtnPress, BtnEdge, Accent, AccentFg, Solid, Hover;
    bool switching;                                   // true when handing over to the conversion window

    static readonly FontFamily Ui = new("Segoe UI Variable Text, Segoe UI");
    static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    readonly StackPanel modelList = new();
    readonly TextBlock aiStatus = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
    readonly StackPanel buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };

    public SettingsWindow()
    {
        MakeBrushes();
        Title = "Convert to Markdown - Settings";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Solid;
        FontFamily = Ui;
        Foreground = Fg;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        Icon = MainWindow.LoadLogo(32);
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 32,
            GlassFrameThickness = new Thickness(-1),
            ResizeBorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false
        });
        Content = Build();
        Refresh();

        // files opened from Explorer while Settings is open go straight to conversion
        Action arrived = () => Dispatcher.BeginInvoke(new Action(OpenConversion));
        Program.FilesArrived += arrived;
        Closed += (_, _) => Program.FilesArrived -= arrived;
    }

    // ================= layout =================
    UIElement Build()
    {
        var root = new StackPanel();

        var head = new Grid { Height = 32, Background = Head };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var logo = new Image { Source = MainWindow.LoadLogo(16), Width = 16, Height = 16, Margin = new Thickness(12, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        RenderOptions.SetBitmapScalingMode(logo, BitmapScalingMode.HighQuality);
        var title = new TextBlock { Text = "Convert to Markdown", FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(title, 1);
        var close = CloseButton();
        Grid.SetColumn(close, 2);
        head.Children.Add(logo);
        head.Children.Add(title);
        head.Children.Add(close);
        root.Children.Add(head);

        var body = new StackPanel { Margin = new Thickness(16, 14, 16, 16) };
        body.Children.Add(new TextBlock { Text = "Settings", FontSize = 13.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
        body.Children.Add(Tick("Describe layout and pictures",
            "Adds a Description section to each page: how it is laid out and what each picture shows. About twice as slow.",
            () => Settings.Describe, v => Settings.Describe = v));
        body.Children.Add(Tick("Use the built-in AI",
            "Reads the text in pictures and screenshots. When off, Windows OCR does it (faster, less accurate).",
            () => Settings.UseAi, v => { Settings.UseAi = v; Refresh(); }));
        aiStatus.Foreground = Sub;
        body.Children.Add(aiStatus);
        body.Children.Add(modelList);
        root.Children.Add(body);

        root.Children.Add(new Border { Height = 1, Background = Line });
        var foot = new Grid { Margin = new Thickness(16, 12, 16, 12), ColumnDefinitions = { new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }, new ColumnDefinition { Width = GridLength.Auto } } };
        Grid.SetColumn(buttons, 1);
        foot.Children.Add(new TextBlock { Text = "Changes are saved automatically", FontSize = 12, Foreground = Sub, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 12, 0) });
        foot.Children.Add(buttons);
        root.Children.Add(foot);
        return root;
    }

    /// <summary>A Windows 11 style tick with a title and a grey explanation under it.</summary>
    UIElement Tick(string text, string explanation, Func<bool> get, Action<bool> set)
    {
        var glyph = new TextBlock
        {
            Text = "", FontFamily = Icons, FontSize = 12, Foreground = AccentFg,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
        var box = new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), Child = glyph, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 12, 0) };
        void Show()
        {
            bool on = get();
            box.Background = on ? Accent : Brushes.Transparent;
            box.BorderBrush = on ? Accent : Sub;
            glyph.Visibility = on ? Visibility.Visible : Visibility.Hidden;
        }
        Show();

        var texts = new StackPanel();
        texts.Children.Add(new TextBlock { Text = text, FontSize = 13 });
        texts.Children.Add(new TextBlock { Text = explanation, FontSize = 12, Foreground = Sub, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
        var row = new Grid { Background = Brushes.Transparent, Margin = new Thickness(0, 6, 0, 6), Cursor = Cursors.Hand };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(texts, 1);
        row.Children.Add(box);
        row.Children.Add(texts);
        row.MouseEnter += (_, _) => { if (!get()) box.Background = Hover; };
        row.MouseLeave += (_, _) => Show();
        row.MouseLeftButtonUp += (_, _) => { set(!get()); Show(); };
        return row;
    }

    /// <summary>Updates the AI line, the model choice and the buttons.</summary>
    void Refresh()
    {
        modelList.Children.Clear();
        var installed = Ai.Models.Where(Ai.HasModel).ToList();
        if (Ai.ServerExe == null || installed.Count == 0)
            aiStatus.Text = "The AI is not set up yet. \"Set up AI\" downloads Qwen3-VL 4B once (about 3 GB).";
        else if (!Settings.UseAi)
            aiStatus.Text = "The AI is turned off.";
        else
            aiStatus.Text = "AI model in use: " + Ai.ModelName;

        if (Settings.UseAi)
        {
            modelList.Children.Add(new TextBlock { Text = "AI model", FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 2) });
            foreach (var m in Ai.Models) modelList.Children.Add(ModelRow(m));
        }

        buttons.Children.Clear();
        if (Ai.ServerExe == null || installed.Count == 0)
            buttons.Children.Add(MakeButton("Set up AI", SetUpAi, false));
        buttons.Children.Add(MakeButton("Convert files…", ConvertFiles, true));
        buttons.Children.Add(MakeButton("Close", Close, false));
    }

    /// <summary>One AI model: tick to use it, plus Download or Delete.</summary>
    UIElement ModelRow(Ai.Model m)
    {
        bool has = Ai.HasModel(m);
        bool inUse = has && Ai.Current.Id == m.Id;
        string note = !has ? $"Not downloaded  ·  {m.Size}"
                    : inUse ? $"In use  ·  {m.Size} on disk"
                    : $"Installed  ·  {m.Size} on disk";
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var tick = Tick(m.Name, note, () => Ai.HasModel(m) && Ai.Current.Id == m.Id, _ =>
        {
            if (!Ai.HasModel(m)) return;                  // download it first
            Settings.Model = m.Id;
            Refresh();
        });
        grid.Children.Add(tick);
        UIElement button = null;
        if (!has)
            button = MakeButton("Download", () => { Program.Pending.Enqueue(Program.SetupAiJob + ":" + m.Id); OpenConversion(); }, false);
        else
            button = MakeButton("Delete", () =>
            {
                var other = Ai.Models.FirstOrDefault(x => x.Id != m.Id && Ai.HasModel(x));
                string after = !inUse ? ""
                    : other != null ? $"\nThe app will switch to {other.Name}."
                    : "\nNo AI model will be left: pictures will be read by Windows OCR until you download one again.";
                var answer = MessageBox.Show(this, $"Delete {m.Name} ({m.Size})?{after}\nYou can download it again later.", "Convert to Markdown",
                                             MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (answer != MessageBoxResult.Yes) return;
                Ai.RemoveModel(m);
                Refresh();
            }, false);
        if (button is FrameworkElement fe)
        {
            fe.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(fe, 1);
            grid.Children.Add(fe);
        }
        return grid;
    }

    // ================= actions =================
    void ConvertFiles()
    {
        var files = Program.AskForFiles();
        if (files.Count == 0) return;
        foreach (var f in files) Program.Pending.Enqueue(f);
        OpenConversion();
    }

    void SetUpAi()
    {
        Program.Pending.Enqueue(Program.SetupAiJob + ":qwen");
        OpenConversion();
    }

    /// <summary>Hands over to the progress window, which converts everything waiting.</summary>
    void OpenConversion()
    {
        if (switching || Program.Pending.IsEmpty) return;
        switching = true;
        var w = new MainWindow();
        Application.Current.MainWindow = w;
        Hide();
        w.ContentRendered += (_, _) => Close();               // close Settings only once the progress window is drawn
        w.Show();
    }

    // ================= look =================
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
        b.MouseLeftButtonUp += (_, _) => Close();
        return b;
    }

    Border MakeButton(string text, Action click, bool primary)
    {
        Brush bg = primary ? Accent : BtnBg;
        var t = new TextBlock { Text = text, FontSize = 12, Foreground = primary ? AccentFg : Fg, HorizontalAlignment = HorizontalAlignment.Center };
        var b = new Border
        {
            CornerRadius = new CornerRadius(4), Background = bg, BorderBrush = primary ? Accent : BtnEdge, BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 5, 12, 6), MinWidth = 80, Margin = new Thickness(8, 0, 0, 0), Child = t
        };
        b.MouseEnter += (_, _) => b.Opacity = primary ? 0.9 : 1;
        b.MouseEnter += (_, _) => { if (!primary) b.Background = BtnHover; };
        b.MouseLeave += (_, _) => { b.Background = bg; b.Opacity = 1; };
        b.MouseLeftButtonDown += (_, e) => { if (!primary) b.Background = BtnPress; else b.Opacity = 0.8; b.CaptureMouse(); e.Handled = true; };
        b.MouseLeftButtonUp += (_, e) =>
        {
            var p = e.GetPosition(b);
            bool inside = p.X >= 0 && p.Y >= 0 && p.X <= b.ActualWidth && p.Y <= b.ActualHeight;
            b.ReleaseMouseCapture();
            b.Background = bg;
            b.Opacity = 1;
            if (inside) click();
        };
        return b;
    }

    static SolidColorBrush C(byte a, byte r, byte g, byte b) => new(Color.FromArgb(a, r, g, b));

    void MakeBrushes()
    {
        if (dark)
        {
            Fg = C(255, 255, 255, 255); Sub = C(255, 0xC5, 0xC5, 0xC5);
            Head = C(0x1F, 255, 255, 255); Line = C(0x26, 255, 255, 255);
            BtnBg = C(0x0F, 255, 255, 255); BtnHover = C(0x15, 255, 255, 255); BtnPress = C(0x08, 255, 255, 255); BtnEdge = C(0x18, 255, 255, 255);
            Accent = C(255, 0x4C, 0xC2, 0xFF); AccentFg = C(255, 0, 0, 0);
            Hover = C(0x15, 255, 255, 255);
            Solid = C(255, 0x20, 0x20, 0x20);
        }
        else
        {
            Fg = C(255, 0x1B, 0x1B, 0x1B); Sub = C(255, 0x5C, 0x5C, 0x5C);
            Head = C(0x99, 255, 255, 255); Line = C(0x1A, 0, 0, 0);
            BtnBg = C(0xB3, 255, 255, 255); BtnHover = C(0x80, 0xF9, 0xF9, 0xF9); BtnPress = C(0x4D, 0xF9, 0xF9, 0xF9); BtnEdge = C(0x29, 0, 0, 0);
            Accent = C(255, 0x00, 0x5F, 0xB8); AccentFg = C(255, 255, 255, 255);
            Hover = C(0x0F, 0, 0, 0);
            Solid = C(255, 0xF3, 0xF3, 0xF3);
        }
    }

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
            long style = GetWindowLongPtr(hwnd, -16).ToInt64();
            style &= ~(0x00080000L | 0x00020000L | 0x00010000L);       // only our own close button
            SetWindowLongPtr(hwnd, -16, new IntPtr(style));
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, 0x0027);
            int v = dark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, 20, ref v, 4);
            v = 2;
            DwmSetWindowAttribute(hwnd, 33, ref v, 4);
            if (Environment.OSVersion.Version.Build >= 22621)
            {
                v = 3;
                if (DwmSetWindowAttribute(hwnd, 38, ref v, 4) == 0)
                {
                    HwndSource.FromHwnd(hwnd).CompositionTarget.BackgroundColor = Colors.Transparent;
                    Background = Brushes.Transparent;
                }
            }
        }
        catch (Exception ex) { Log.Write("settings window effects: " + ex.Message); }
    }
}
