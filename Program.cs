// Convert to Markdown - entry point.
// Several files opened at once (Explorer starts one copy per file) share one window:
// the first copy owns the window, the others hand their files to it through a named pipe.

using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;

namespace ConvertToMarkdown;

static class Program
{
    const string MutexName = @"Local\ConvertToMarkdown.Single";
    const string PipeName = "ConvertToMarkdown.Pipe";

    /// <summary>
    /// True for the Microsoft Store version: the app runs from its package folder (which holds AppxManifest.xml).
    /// Windows then installs, updates and uninstalls it, and adds the menu entries itself.
    /// </summary>
    public static readonly bool IsStore = DetectStore();

    /// <summary>Settings, AI models, cache and log. The Store version uses its own app-data folder, which Windows deletes on uninstall.</summary>
    public static readonly string DataDir = IsStore ? StoreDataDir()
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ConvertToMarkdown");
    public static readonly string CacheDir = Path.Combine(DataDir, "cache");

    public static readonly ConcurrentQueue<string> Pending = new();
    public const string SetupAiJob = "::setup-ai";
    public static event Action FilesArrived;

    [STAThread]
    static int Main(string[] args)
    {
        // started by Windows for the right-click menu entry, or as administrator to trust the menu package
        if (args.Any(a => a.Equals("-Embedding", StringComparison.OrdinalIgnoreCase) || a.Equals("/Embedding", StringComparison.OrdinalIgnoreCase)))
        {
            MenuCommand.RunServer();
            return 0;
        }
        UseOwnTaskbarIcon();
        int trust = Array.IndexOf(args, "--trust-cert");
        if (trust >= 0 && trust + 1 < args.Length)
            return MenuPackage.TrustCertificate(args[trust + 1]);

        Directory.CreateDirectory(CacheDir);

        // Store version, first start without files: the welcome screens (choose the AI) instead of Setup
        if (IsStore && !Settings.Exists && !args.Any(a => !a.StartsWith("--")))
        {
            var welcomeApp = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnMainWindowClose };
            return welcomeApp.Run(new SetupWindow(firstRun: true));
        }

        // Uninstall (from Settings > Apps) and Setup ("Convert to Markdown Setup.exe", or --install).
        // Not in the Store version: Windows installs and uninstalls it.
        if (!IsStore && args.Contains("--uninstall"))
        {
            Installer.UninstallInteractive();
            return 0;
        }
        string myName = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "");
        if (!IsStore && (args.Contains("--install") || myName.Contains("Setup", StringComparison.OrdinalIgnoreCase)))
        {
            var setupApp = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnMainWindowClose };
            return setupApp.Run(new SetupWindow());
        }

        // AI commands: --setup-ai, --remove-ai, --ai on|off
        if (args.Contains("--remove-ai"))
        {
            Ai.Remove();
            System.Windows.MessageBox.Show("The AI was removed. Conversions now use Windows OCR for pictures.", "Convert to Markdown");
            return 0;
        }
        if (args.Contains("--check-ai"))
        {
            string report = Ai.CheckAsync().GetAwaiter().GetResult();
            Log.Write("check-ai:\n" + report);
            System.Windows.MessageBox.Show(report, "Convert to Markdown - AI check");
            return 0;
        }
        int aiFlag = Array.IndexOf(args, "--ai");
        if (aiFlag >= 0 && aiFlag + 1 < args.Length)
        {
            Settings.UseAi = args[aiFlag + 1].Equals("on", StringComparison.OrdinalIgnoreCase);
            System.Windows.MessageBox.Show(Settings.UseAi ? "The AI is turned on." : "The AI is turned off (Windows OCR is used for pictures).", "Convert to Markdown");
            return 0;
        }

        // --remove-model gemma|qwen : delete one model's files
        int removeFlag = Array.IndexOf(args, "--remove-model");
        if (removeFlag >= 0 && removeFlag + 1 < args.Length)
        {
            var m = Ai.Find(args[removeFlag + 1]);
            if (m != null) Ai.RemoveModel(m);
            System.Windows.MessageBox.Show(m == null ? "Unknown model. Use gemma or qwen." : $"{m.Name} was removed. Now using: {Ai.ModelName}.", "Convert to Markdown");
            return 0;
        }

        // --ai-model gemma|qwen : choose which installed model to use
        int modelFlag = Array.IndexOf(args, "--ai-model");
        if (modelFlag >= 0 && modelFlag + 1 < args.Length)
        {
            var m = Ai.Find(args[modelFlag + 1]);
            string msg = m == null ? "Unknown model. Use gemma or qwen."
                : !Ai.HasModel(m) ? $"{m.Name} is not downloaded yet. Run its setup first."
                : $"Now using {m.Name}.";
            if (m != null && Ai.HasModel(m)) Settings.Model = m.Id;
            System.Windows.MessageBox.Show(msg, "Convert to Markdown");
            return 0;
        }

        int setupFlag = Array.IndexOf(args, "--setup-ai");
        string setupId = setupFlag >= 0 && setupFlag + 1 < args.Length && !args[setupFlag + 1].StartsWith("--") ? args[setupFlag + 1] : Ai.Current.Id;
        var files = setupFlag >= 0
            ? new List<string> { SetupAiJob + ":" + setupId }
            : args.Where(a => !a.StartsWith("--")).Select(a => Path.GetFullPath(a.Trim('"'))).ToList();
        // no file (or --settings): open the Settings window, which also has "Convert files…"
        bool showSettings = files.Count == 0 || args.Contains("--settings");
        if (showSettings) files.Clear();

        using var mutex = new Mutex(true, MutexName, out bool first);
        if (!first && !showSettings && SendToRunning(files))
            return 0;                       // the open window takes them

        if (first)
        {
            Log.Reset();
            CleanCache();
            StartPipeServer();
        }
        foreach (var f in files) Pending.Enqueue(f);

        var app = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnMainWindowClose };
        return app.Run(showSettings ? new SettingsWindow() : new MainWindow());
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern int GetCurrentPackageFullName(ref int length, System.Text.StringBuilder name);

    static bool DetectStore()
    {
        try
        {
            int len = 0;
            if (GetCurrentPackageFullName(ref len, null) == 15700) return false;      // no package identity
            return File.Exists(Path.Combine(AppContext.BaseDirectory, "AppxManifest.xml")); // not the small menu package
        }
        catch { return false; }
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    /// <summary>
    /// When started from the right-click menu the app runs inside its package, and Windows takes the taskbar icon
    /// from the package. Naming the package's app here makes Windows find it (otherwise it shows a blank grey square).
    /// </summary>
    static void UseOwnTaskbarIcon()
    {
        try
        {
            int len = 0;
            if (GetCurrentPackageFullName(ref len, null) == 15700) return;       // not inside a package: the .exe icon is used
            SetCurrentProcessExplicitAppUserModelID(Windows.ApplicationModel.Package.Current.Id.FamilyName + "!App");
        }
        catch { }
    }

    static string StoreDataDir()
    {
        try { return Windows.Storage.ApplicationData.Current.LocalFolder.Path; }
        catch { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ConvertToMarkdown"); }
    }

    internal static List<string> AskForFiles()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Convert to Markdown",
            Multiselect = true,
            Filter = "PDF, Office files and pictures|*.pdf;*.docx;*.docm;*.pptx;*.pptm;*.xlsx;*.xlsm;*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff;*.gif|All files|*.*"
        };
        return dlg.ShowDialog() == true ? dlg.FileNames.ToList() : new List<string>();
    }

    static bool SendToRunning(List<string> files)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
            pipe.Connect(3000);
            var w = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
            var r = new StreamReader(pipe, Encoding.UTF8);
            foreach (var f in files) w.WriteLine(f);
            w.WriteLine("");                // end of the list
            return r.ReadLine() == "ok";
        }
        catch
        {
            return false;                   // convert in this copy instead
        }
    }

    static void StartPipeServer()
    {
        var t = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte);
                    pipe.WaitForConnection();
                    var r = new StreamReader(pipe, Encoding.UTF8);
                    var w = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
                    string line;
                    while ((line = r.ReadLine()) != null && line.Length > 0)
                        Pending.Enqueue(line);
                    w.WriteLine("ok");
                    FilesArrived?.Invoke();
                }
                catch (Exception e)
                {
                    Log.Write("pipe: " + e.Message);
                    Thread.Sleep(200);
                }
            }
        }) { IsBackground = true };
        t.Start();
    }

    static void CleanCache()
    {
        try
        {
            foreach (var f in Directory.GetFiles(CacheDir))
                if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-1))
                    File.Delete(f);
        }
        catch { }
    }
}

static class Log
{
    static readonly object Gate = new();
    static string FilePath => Path.Combine(Program.DataDir, "last_run.log");

    public static void Reset()
    {
        try { File.WriteAllText(FilePath, ""); } catch { }
    }

    public static void Write(string s)
    {
        try
        {
            lock (Gate)
                File.AppendAllText(FilePath, $"{DateTime.Now:HH:mm:ss} {s}{Environment.NewLine}");
        }
        catch { }
    }
}
