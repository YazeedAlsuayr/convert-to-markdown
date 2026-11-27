// Installing for the current user only (no administrator needed):
// the app in %LOCALAPPDATA%\Programs\ConvertToMarkdown, "Open with" and "Send to" entries,
// a Start menu shortcut, and an entry in Settings > Apps with a working Uninstall.

using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ConvertToMarkdown;

static class Installer
{
    public const string Version = "2.0.0";
    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "ConvertToMarkdown");
    public static string Exe => Path.Combine(Dir, "ConvertToMarkdown.exe");
    public static bool IsInstalled => File.Exists(Exe);

    internal static readonly string[] Types =
        { ".pdf", ".docx", ".docm", ".pptx", ".pptm", ".xlsx", ".xlsm", ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff", ".gif",
          ".doc", ".ppt", ".xls" };   // old Office formats: not converted, but the menu shows how to save them as new ones
    const string ProgId = "ConvertToMarkdown.File";
    const string AppKey = @"Software\Classes\Applications\ConvertToMarkdown.exe";
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\ConvertToMarkdown";

    static string StartMenuLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Convert to Markdown.lnk");
    static string SendToLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.SendTo), "Convert to Markdown.lnk");

    /// <summary>The Setup file must be the single-file app made by Build.cmd (not the quick test build).</summary>
    public static bool CanInstallFromHere =>
        !File.Exists(Path.Combine(AppContext.BaseDirectory, "ConvertToMarkdown.dll"));

    // ---------------- the old Python version ----------------
    const string OldProgId = "MarkdownTool.Convert";
    static readonly string[] OldMenuKeys = { @"Software\Classes\*\shell\MarkdownTool", @"Software\Classes\Directory\shell\MarkdownTool" };
    static readonly string[] OldTypes =
        { ".pdf", ".docx", ".pptx", ".xlsx", ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff", ".webp", ".html", ".htm", ".csv", ".json", ".xml", ".epub", ".txt" };

    public static bool OldVersionFound =>
        KeyExists(@"Software\Classes\" + OldProgId) || OldMenuKeys.Any(KeyExists);

    /// <summary>Removes the Python version's menu entries (its files are left alone).</summary>
    public static void RemoveOldMenus()
    {
        foreach (var k in OldMenuKeys) DeleteTree(k);
        DeleteTree(@"Software\Classes\" + OldProgId);
        foreach (var ext in OldTypes)
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{ext}\OpenWithProgids", true);
                k?.DeleteValue(OldProgId, false);
            }
            catch { }
    }

    // ---------------- install steps ----------------
    public static void CopyApp()
    {
        string me = Environment.ProcessPath;
        if (string.Equals(Path.GetFullPath(me), Path.GetFullPath(Exe), StringComparison.OrdinalIgnoreCase)) return;
        foreach (var p in Process.GetProcessesByName("ConvertToMarkdown"))          // an open copy would lock the file
            try { if (p.Id != Environment.ProcessId) { p.Kill(true); p.WaitForExit(3000); } } catch { }
        Directory.CreateDirectory(Dir);
        for (int attempt = 0; ; attempt++)
        {
            try { File.Copy(me, Exe, true); break; }
            catch (IOException) when (attempt < 10) { Thread.Sleep(500); }
        }
    }

    public static void AddOpenWith()
    {
        string cmd = $"\"{Exe}\" \"%1\"";
        string icon = $"\"{Exe}\",0";
        DeleteTree(AppKey + @"\SupportedTypes");                  // drop types the old version listed
        Set(AppKey, "FriendlyAppName", "Convert to Markdown");
        Set(AppKey + @"\DefaultIcon", "", icon);
        Set(AppKey + @"\shell\open\command", "", cmd);
        Set(@"Software\Classes\" + ProgId, "", "Convert to Markdown");
        Set(@"Software\Classes\" + ProgId + @"\DefaultIcon", "", icon);
        Set(@"Software\Classes\" + ProgId + @"\shell\open", "FriendlyAppName", "Convert to Markdown");
        Set(@"Software\Classes\" + ProgId + @"\shell\open\command", "", cmd);
        foreach (var ext in Types)
        {
            Set($@"Software\Classes\{ext}\OpenWithProgids", ProgId, "");
            Set(AppKey + @"\SupportedTypes", ext, "");
        }
        RefreshShell();
    }

    public static void RemoveSendTo()
    {
        try { if (File.Exists(SendToLink)) File.Delete(SendToLink); } catch { }
    }
    public static void AddStartMenu() => Shortcut(StartMenuLink, Exe);

    public static void AddUninstallEntry()
    {
        Set(UninstallKey, "DisplayName", "Convert to Markdown");
        Set(UninstallKey, "DisplayVersion", Version);
        Set(UninstallKey, "Publisher", "Yazeed Alsuayr");
        Set(UninstallKey, "DisplayIcon", Exe);
        Set(UninstallKey, "InstallLocation", Dir);
        Set(UninstallKey, "UninstallString", $"\"{Exe}\" --uninstall");
        Set(UninstallKey, "NoModify", 1);
        Set(UninstallKey, "NoRepair", 1);
        try { Set(UninstallKey, "EstimatedSize", (int)(new FileInfo(Exe).Length / 1024)); } catch { }
    }

    // ---------------- uninstall ----------------
    public static void UninstallInteractive()
    {
        var ok = System.Windows.MessageBox.Show("Uninstall Convert to Markdown?", "Convert to Markdown",
                                                System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
        if (ok != System.Windows.MessageBoxResult.Yes) return;
        bool removeAi = false;
        if (Directory.Exists(Ai.Dir))
        {
            long bytes = 0;
            try { bytes = new DirectoryInfo(Ai.Dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length); } catch { }
            removeAi = System.Windows.MessageBox.Show($"Also delete the AI (about {bytes / 1e9:0.0} GB)?\nChoose No to keep it for a later install.",
                           "Convert to Markdown", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question)
                       == System.Windows.MessageBoxResult.Yes;
        }
        Ai.Stop();

        MenuPackage.Remove();
        DeleteTree(AppKey);
        DeleteTree(@"Software\Classes\" + ProgId);
        foreach (var ext in Types)
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{ext}\OpenWithProgids", true);
                k?.DeleteValue(ProgId, false);
            }
            catch { }
        DeleteTree(UninstallKey);
        foreach (var lnk in new[] { StartMenuLink, SendToLink })
            try { if (File.Exists(lnk)) File.Delete(lnk); } catch { }
        RefreshShell();

        try
        {
            if (removeAi) Directory.Delete(Program.DataDir, true);
            else foreach (var f in new[] { "cache", "last_run.log" })
                {
                    string p = Path.Combine(Program.DataDir, f);
                    if (Directory.Exists(p)) Directory.Delete(p, true);
                    else if (File.Exists(p)) File.Delete(p);
                }
        }
        catch { }

        // the running app cannot delete itself: a hidden command removes the folder a moment after it closes
        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c timeout /t 2 /nobreak >nul & rmdir /s /q \"{Dir}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden
            });
        }
        catch { }
        System.Windows.MessageBox.Show("Convert to Markdown was uninstalled.", "Convert to Markdown");
    }

    // ---------------- helpers ----------------
    static void Set(string key, string name, object value)
    {
        using var k = Registry.CurrentUser.CreateSubKey(key);
        k.SetValue(name ?? "", value);
    }

    static bool KeyExists(string key)
    {
        using var k = Registry.CurrentUser.OpenSubKey(key);
        return k != null;
    }

    static void DeleteTree(string key)
    {
        try { Registry.CurrentUser.DeleteSubKeyTree(key, false); } catch { }
    }

    static void Shortcut(string lnk, string target)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(lnk));
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            dynamic shell = Activator.CreateInstance(shellType);
            dynamic s = shell.CreateShortcut(lnk);
            s.TargetPath = target;
            s.WorkingDirectory = Path.GetDirectoryName(target);
            s.IconLocation = target + ",0";
            s.Description = "Convert to Markdown";
            s.Save();
            Marshal.FinalReleaseComObject(s);
            Marshal.FinalReleaseComObject(shell);
        }
        catch (Exception e) { Log.Write("shortcut " + lnk + ": " + e.Message); }
    }

    [DllImport("shell32.dll")] static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
    static void RefreshShell() { try { SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero); } catch { } }
}
