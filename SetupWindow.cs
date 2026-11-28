// Setup with welcome screens: Welcome -> choose the AI -> where to add it -> installing -> done.
// The Store version (firstRun) shows only Welcome -> choose the AI -> downloading -> done:
// Windows has already installed the app and added the menu entries.
// Same frosted Windows 11 look as the rest of the app.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;

namespace ConvertToMarkdown;

class SetupWindow : Window
{
    readonly bool dark = MainWindow.IsDarkMode();
    Brush Fg, Sub, Head, Line, Card, BtnBg, BtnHover, BtnPress, BtnEdge, Accent, AccentFg, AccentSoft, Hover, Solid, Track, Bar, Green, Orange;
    static readonly FontFamily Ui = new("Segoe UI Variable Text, Segoe UI");
    static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    readonly StackPanel page = new() { Margin = new Thickness(24, 20, 24, 20) };
    readonly StackPanel buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
    readonly TextBlock footNote = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };

    // choices
    string model = "qwen";                  // qwen, gemma or none
    bool describe = false, openWith = true, startMenu = true, topMenu = MenuPackage.Available;
    CancellationTokenSource cts;
    bool installing;
    readonly bool firstRun;

    public SetupWindow(bool firstRun = false)
    {
        this.firstRun = firstRun;
        MakeBrushes();
        Title = firstRun ? "Welcome to Convert to Markdown" : "Convert to Markdown Setup";
        Width = 500;
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
            CaptionHeight = 32, GlassFrameThickness = new Thickness(-1), ResizeBorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0), UseAeroCaptionButtons = false
        });
        footNote.Foreground = Sub;

        var root = new StackPanel();
        root.Children.Add(HeaderBar());
        root.Children.Add(page);
        root.Children.Add(new Border { Height = 1, Background = Line });
        // text on the left, buttons on the right: the text wraps instead of running under the buttons
        var foot = new Grid { Margin = new Thickness(24, 12, 24, 12), ColumnDefinitions = { new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }, new ColumnDefinition { Width = GridLength.Auto } } };
        footNote.TextWrapping = TextWrapping.Wrap;
        footNote.Margin = new Thickness(0, 0, 12, 0);
        Grid.SetColumn(buttons, 1);
        foot.Children.Add(footNote);
        foot.Children.Add(buttons);
        root.Children.Add(foot);
        Content = root;
        Closing += (_, e) =>
        {
            if (installing) { cts?.Cancel(); }
            Ai.Stop();
        };
        ShowWelcome();
    }

    // ======================= pages =======================
    void ShowWelcome()
    {
        Clear();
        var logo = new Image { Source = MainWindow.LoadLogo(64), Width = 56, Height = 56, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 14) };
        RenderOptions.SetBitmapScalingMode(logo, BitmapScalingMode.HighQuality);
        page.Children.Add(logo);
        page.Children.Add(Heading("Convert to Markdown"));
        page.Children.Add(Paragraph("Turns PDFs, Word, PowerPoint, Excel files and pictures into Markdown: typed text, tables, highlights, " +
                                    "your handwritten notes, and the text inside pictures and screenshots."));
        page.Children.Add(Paragraph("Everything runs on this PC. Your files are never uploaded."));
        if (firstRun) { }
        else if (!Installer.CanInstallFromHere)
            page.Children.Add(Paragraph("This is the quick test build. To install, run \"Convert to Markdown Setup.exe\" from the App folder made by Build.cmd.", true));
        else if (Installer.IsInstalled)
            page.Children.Add(Paragraph("Convert to Markdown is already installed. Setup will update it and keep your settings.", true));
        footNote.Text = firstRun ? "Step 1 of 2" : "Step 1 of 3";
        Buttons((firstRun ? "Skip" : "Cancel", firstRun ? (Action)SkipWelcome : Close, false), ("Next", ShowModel, true));
        if (!firstRun && !Installer.CanInstallFromHere) buttons.Children.RemoveAt(1);
    }

    void ShowModel()
    {
        Clear();
        page.Children.Add(Heading("Choose the AI"));
        page.Children.Add(Paragraph("The AI reads the text in pictures, screenshots and handwriting, and can describe pages. " +
                                    "It runs on your graphics card, offline, and is downloaded once. You can change this later in Settings."));
        page.Children.Add(ModelCard("qwen", "Qwen3-VL 4B", new[] { ("Recommended", Accent), ("Faster", Green) },
            "About 3 GB. Best at copying text exactly, including Arabic and screenshots."));
        page.Children.Add(ModelCard("gemma", "Gemma 4", new[] { ("Slower", Orange) },
            "About 5.2 GB. Writes longer descriptions; good at spotting ticked or circled answers."));
        page.Children.Add(ModelCard("none", "No AI", new[] { ("Fastest", Green) },
            "Nothing to download. A quick built-in reader reads the text in pictures and handwriting (less accurate). Typed text works the same."));
        if (model != "none")
            page.Children.Add(Tick("Describe layout and pictures",
                "Adds a Description section to each page: how it is laid out and what each picture shows. About twice as slow.",
                () => describe, v => describe = v));
        if (firstRun)
        {
            var m = Ai.Find(model);
            footNote.Text = m == null ? "Step 2 of 2 · nothing to download"
                          : Ai.HasModel(m) ? "Step 2 of 2 · already downloaded"
                          : $"Step 2 of 2 · download {m.Size.Replace("about ", "")}";
            Buttons(("Back", ShowWelcome, false), (m != null && !Ai.HasModel(m) ? "Download" : "Start", () => _ = InstallAsync(), true));
        }
        else
        {
            footNote.Text = "Step 2 of 3";
            Buttons(("Back", ShowWelcome, false), ("Next", ShowPlaces, true));
        }
    }

    void ShowPlaces()
    {
        Clear();
        page.Children.Add(Heading("Where to find it"));
        if (MenuPackage.Available)
            page.Children.Add(Tick("Top of the right-click menu (Windows 11)",
                "Shows Convert to Markdown right in the right-click menu, next to Open with. Windows asks for administrator permission once.",
                () => topMenu, v => topMenu = v));
        else
            page.Children.Add(Paragraph("The top-of-menu entry is not included in this Setup file (Build.cmd could not make it).", true));
        page.Children.Add(Tick("Right-click → Open with",
            "Right-click a PDF, Word, PowerPoint, Excel file or picture → Open with → Convert to Markdown.", () => openWith, v => openWith = v));
        page.Children.Add(Tick("Start menu",
            "Opens Settings, where you can also convert files and change the AI.", () => startMenu, v => startMenu = v));
        var m = Ai.Find(model);
        footNote.Text = m == null ? "Nothing to download"
                      : Ai.HasModel(m) ? $"{m.Name} is already downloaded"
                      : $"Will download {m.Name} ({m.Size})";
        Buttons(("Back", ShowModel, false), (Installer.IsInstalled ? "Update" : "Install", () => _ = InstallAsync(), true));
    }

    async Task InstallAsync()
    {
        Clear();
        installing = true;
        cts = new CancellationTokenSource();
        page.Children.Add(Heading(firstRun ? "Getting ready" : "Installing"));
        var step = Paragraph(firstRun ? "Saving your choices" : "Copying the app");
        var detail = new TextBlock { FontSize = 12, Foreground = Sub, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
        var fill = new System.Windows.Shapes.Rectangle { Fill = Bar, HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
        var track = new Border { Height = 10, Margin = new Thickness(0, 14, 0, 0), Background = Track, BorderBrush = Line, BorderThickness = new Thickness(1), Child = fill };
        double value = 0;
        void SetBar(double v) { value = Math.Clamp(v, 0, 1); fill.Width = Math.Max(0, (track.ActualWidth - 2) * value); }
        track.SizeChanged += (_, _) => SetBar(value);
        page.Children.Add(step);
        page.Children.Add(detail);
        page.Children.Add(track);
        footNote.Text = "";
        Buttons(("Cancel", () => { cts.Cancel(); detail.Text = "Stopping…"; }, false));

        string problem = null;
        try
        {
            if (!firstRun) await Task.Run(() =>
            {
                Installer.CopyApp();
                if (Installer.OldVersionFound) Installer.RemoveOldMenus();      // an older version's duplicate entries
                if (openWith) Installer.AddOpenWith();
                Installer.RemoveSendTo();
                if (startMenu) Installer.AddStartMenu();
                Installer.AddUninstallEntry();
            });
            if (topMenu && !firstRun)
            {
                step.Text = "Adding Convert to Markdown to the right-click menu";
                string menuProblem = await Task.Run(() => MenuPackage.Install());
                if (menuProblem != null) problem = menuProblem;
            }
            Settings.UseAi = model != "none";
            Settings.Describe = model != "none" && describe;
            SetBar(0.05);

            var m = Ai.Find(model);
            if (m != null)
            {
                Settings.Model = m.Id;
                if (!Ai.HasModel(m) || Ai.ServerExe == null)
                {
                    step.Text = $"Downloading {m.Name}";
                    var progress = new Progress<Step>(s =>
                    {
                        if (!string.IsNullOrEmpty(s.Detail)) detail.Text = s.Detail;
                        SetBar(0.05 + 0.95 * s.Frac);
                    });
                    var token = cts.Token;
                    try { await Task.Run(() => Ai.SetupAsync(m, progress, token)); }
                    catch (OperationCanceledException) { problem = (problem == null ? "" : problem + "\n\n") + "The AI download was stopped. Download it any time from Settings (it continues where it stopped)."; }
                    catch (Exception e) { problem = (problem == null ? "" : problem + "\n\n") + "The app is installed, but the AI could not be set up: " + e.Message + " You can try again from Settings."; Log.Write("setup AI: " + e); }
                }
            }
            SetBar(1);
        }
        catch (Exception e)
        {
            Log.Write("install: " + e);
            installing = false;
            ShowDone(false, "Setup could not finish: " + e.Message);
            return;
        }
        installing = false;
        ShowDone(true, problem);
    }

    void ShowDone(bool installed, string problem)
    {
        Clear();
        page.Children.Add(Heading(installed ? "Convert to Markdown is ready" : "Setup did not finish"));
        if (installed && firstRun)
        {
            page.Children.Add(Paragraph("Right-click a PDF, Word, PowerPoint or Excel file, or a picture → Convert to Markdown. The .md file appears next to it."));
            page.Children.Add(Paragraph("Several files at once: select them all, then right-click."));
            page.Children.Add(Paragraph("To change the AI later, open Convert to Markdown from the Start menu."));
        }
        else if (installed)
        {
            if (topMenu && problem == null)
                page.Children.Add(Paragraph("Right-click a PDF, Word or PowerPoint file → Convert to Markdown. The .md file appears next to it. " +
                                            "(If it doesn't show yet, sign out and back in once.)"));
            else if (openWith)
                page.Children.Add(Paragraph("Right-click a PDF, Word or PowerPoint file → Open with → Convert to Markdown. The .md file appears next to it."));
            page.Children.Add(Paragraph("Several files at once: select them all, then right-click."));
            if (startMenu) page.Children.Add(Paragraph("Settings are in the Start menu under Convert to Markdown."));
        }
        if (problem != null) page.Children.Add(Paragraph(problem, true));
        footNote.Text = "";
        if (installed && firstRun && Ai.Enabled)
            // the AI is ready: test this PC's speed and pick Faster or More accurate
            Buttons(("Finish", Close, false), ("Set up for this PC", () => OpenSettingsHere(true), true));
        else if (installed && firstRun)
            Buttons(("Open Settings", () => OpenSettingsHere(false), false), ("Finish", Close, true));
        else if (installed)
            Buttons(("Open Settings", () => { try { Process.Start(new ProcessStartInfo(Installer.Exe) { UseShellExecute = true }); } catch { } Close(); }, false),
                    ("Finish", Close, true));
        else
            Buttons(("Close", Close, true));
    }

    /// <summary>Store version: "Skip" keeps the defaults (no AI download yet; Windows OCR for pictures).</summary>
    void SkipWelcome()
    {
        Settings.UseAi = false;
        Settings.Describe = false;
        Close();
    }

    /// <summary>Store version: opens Settings in this same copy of the app.</summary>
    void OpenSettingsHere(bool pcSetup)
    {
        var s = new SettingsWindow(pcSetup);
        Application.Current.MainWindow = s;
        s.Show();
        Close();
    }

    // ======================= pieces =======================
    void Clear() => page.Children.Clear();

    TextBlock Heading(string text) =>
        new() { Text = text, FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) };

    TextBlock Paragraph(string text, bool note = false) =>
        new() { Text = text, FontSize = 13, TextWrapping = TextWrapping.Wrap, Foreground = note ? Sub : Fg, Margin = new Thickness(0, 0, 0, 8) };

    void Buttons(params (string text, Action click, bool primary)[] list)
    {
        buttons.Children.Clear();
        foreach (var (text, click, primary) in list) buttons.Children.Add(MakeButton(text, click, primary));
    }

    /// <summary>A choice card for one AI option, with coloured tags like "Recommended" and "Faster".</summary>
    UIElement ModelCard(string id, string name, (string text, Brush color)[] tags, string text)
    {
        bool selected = model == id;
        var m = Ai.Find(id);
        if (m != null && Ai.HasModel(m)) text = "Already downloaded. " + text;

        var dot = new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Fill = AccentFg, Visibility = selected ? Visibility.Visible : Visibility.Hidden };
        var ring = new Grid { Width = 20, Height = 20, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 12, 0) };
        ring.Children.Add(new System.Windows.Shapes.Ellipse { Fill = selected ? Accent : Brushes.Transparent, Stroke = selected ? Accent : Sub, StrokeThickness = 1 });
        ring.Children.Add(dot);

        var titleRow = new WrapPanel();
        titleRow.Children.Add(new TextBlock { Text = name, FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
        foreach (var (t, color) in tags)
            titleRow.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 1, 6, 2), Margin = new Thickness(0, 0, 6, 0),
                BorderBrush = color, BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = t, FontSize = 11, Foreground = color }
            });
        var texts = new StackPanel();
        texts.Children.Add(titleRow);
        texts.Children.Add(new TextBlock { Text = text, FontSize = 12, Foreground = Sub, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(texts, 1);
        grid.Children.Add(ring);
        grid.Children.Add(texts);

        var card = new Border
        {
            CornerRadius = new CornerRadius(6), Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(0, 0, 0, 8),
            BorderThickness = new Thickness(selected ? 2 : 1), BorderBrush = selected ? Accent : Line,
            Background = selected ? AccentSoft : Card, Cursor = Cursors.Hand, Child = grid
        };
        card.MouseEnter += (_, _) => { if (!selected) card.Background = Hover; };
        card.MouseLeave += (_, _) => card.Background = selected ? AccentSoft : Card;
        card.MouseLeftButtonUp += (_, _) => { model = id; ShowModel(); };
        return card;
    }

    UIElement Tick(string text, string explanation, Func<bool> get, Action<bool> set)
    {
        var glyph = new TextBlock { Text = "", FontFamily = Icons, FontSize = 12, Foreground = AccentFg, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
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
        var row = new Grid { Background = Brushes.Transparent, Margin = new Thickness(0, 8, 0, 4), Cursor = Cursors.Hand };
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

    UIElement HeaderBar()
    {
        var head = new Grid { Height = 32, Background = Head };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var logo = new Image { Source = MainWindow.LoadLogo(16), Width = 16, Height = 16, Margin = new Thickness(12, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        RenderOptions.SetBitmapScalingMode(logo, BitmapScalingMode.HighQuality);
        var title = new TextBlock { Text = "Convert to Markdown Setup", FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(title, 1);
        var glyph = new TextBlock { Text = "", FontFamily = Icons, FontSize = 10, Foreground = Fg, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var close = new Border { Width = 46, Height = 32, Background = Brushes.Transparent, Child = glyph };
        WindowChrome.SetIsHitTestVisibleInChrome(close, true);
        close.MouseEnter += (_, _) => { close.Background = new SolidColorBrush(Color.FromRgb(0xC4, 0x2B, 0x1C)); glyph.Foreground = Brushes.White; };
        close.MouseLeave += (_, _) => { close.Background = Brushes.Transparent; glyph.Foreground = Fg; };
        close.MouseLeftButtonUp += (_, _) => Close();
        Grid.SetColumn(close, 2);
        head.Children.Add(logo);
        head.Children.Add(title);
        head.Children.Add(close);
        return head;
    }

    Border MakeButton(string text, Action click, bool primary)
    {
        Brush bg = primary ? Accent : BtnBg;
        var t = new TextBlock { Text = text, FontSize = 12, Foreground = primary ? AccentFg : Fg, HorizontalAlignment = HorizontalAlignment.Center };
        var b = new Border
        {
            CornerRadius = new CornerRadius(4), Background = bg, BorderBrush = primary ? Accent : BtnEdge, BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 5, 12, 6), MinWidth = 90, Margin = new Thickness(8, 0, 0, 0), Child = t
        };
        b.MouseEnter += (_, _) => { if (primary) b.Opacity = 0.9; else b.Background = BtnHover; };
        b.MouseLeave += (_, _) => { b.Background = bg; b.Opacity = 1; };
        b.MouseLeftButtonDown += (_, e) => { if (primary) b.Opacity = 0.8; else b.Background = BtnPress; b.CaptureMouse(); e.Handled = true; };
        b.MouseLeftButtonUp += (_, e) =>
        {
            var p = e.GetPosition(b);
            bool inside = p.X >= 0 && p.Y >= 0 && p.X <= b.ActualWidth && p.Y <= b.ActualHeight;
            b.ReleaseMouseCapture();
            b.Background = bg;
            b.Opacity = 1;
            if (inside) Dispatcher.BeginInvoke(click);            // after this click finishes (pages replace the buttons)
        };
        return b;
    }

    static SolidColorBrush C(byte a, byte r, byte g, byte b) => new(Color.FromArgb(a, r, g, b));

    void MakeBrushes()
    {
        if (dark)
        {
            Fg = C(255, 255, 255, 255); Sub = C(255, 0xC5, 0xC5, 0xC5);
            Head = C(0x1F, 255, 255, 255); Line = C(0x26, 255, 255, 255); Card = C(0x0A, 255, 255, 255);
            BtnBg = C(0x0F, 255, 255, 255); BtnHover = C(0x15, 255, 255, 255); BtnPress = C(0x08, 255, 255, 255); BtnEdge = C(0x18, 255, 255, 255);
            Accent = C(255, 0x4C, 0xC2, 0xFF); AccentFg = C(255, 0, 0, 0); AccentSoft = C(0x1A, 0x4C, 0xC2, 0xFF);
            Hover = C(0x15, 255, 255, 255); Solid = C(255, 0x20, 0x20, 0x20); Track = C(0x66, 0, 0, 0);
            Green = C(255, 0x6C, 0xCB, 0x5F); Orange = C(255, 0xFC, 0xA5, 0x4B);
        }
        else
        {
            Fg = C(255, 0x1B, 0x1B, 0x1B); Sub = C(255, 0x5C, 0x5C, 0x5C);
            Head = C(0x99, 255, 255, 255); Line = C(0x1A, 0, 0, 0); Card = C(0x80, 255, 255, 255);
            BtnBg = C(0xB3, 255, 255, 255); BtnHover = C(0x80, 0xF9, 0xF9, 0xF9); BtnPress = C(0x4D, 0xF9, 0xF9, 0xF9); BtnEdge = C(0x29, 0, 0, 0);
            Accent = C(255, 0x00, 0x5F, 0xB8); AccentFg = C(255, 255, 255, 255); AccentSoft = C(0x14, 0x00, 0x5F, 0xB8);
            Hover = C(0x0F, 0, 0, 0); Solid = C(255, 0xF3, 0xF3, 0xF3); Track = C(0x1A, 0, 0, 0);
            Green = C(255, 0x0F, 0x7B, 0x0F); Orange = C(255, 0xC2, 0x5B, 0x00);
        }
        Bar = C(255, 0x06, 0xB0, 0x25);
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
            style &= ~(0x00080000L | 0x00020000L | 0x00010000L);
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
        catch (Exception ex) { Log.Write("setup window effects: " + ex.Message); }
    }
}
