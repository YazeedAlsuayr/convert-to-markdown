// The Windows 11 right-click menu entry ("Convert to Markdown" at the top of the menu).
// Windows starts the app with -Embedding and asks it, through COM, for the entry's name, icon and
// whether to show it; clicking it starts a normal conversion with the selected files.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;

namespace ConvertToMarkdown;

static class MenuCommand
{
    public const string Clsid = "6E4B2D1C-8F3A-4C7B-9A21-5D0E7F3B8C42";       // must match Package\AppxManifest.xml
    static DateTime lastUse = DateTime.Now;
    internal static void Touch() => lastUse = DateTime.Now;

    [DllImport("ole32.dll")]
    static extern int CoRegisterClassObject(ref Guid clsid, [MarshalAs(UnmanagedType.IUnknown)] object factory, uint context, uint flags, out uint cookie);
    [DllImport("ole32.dll")] static extern int CoRevokeClassObject(uint cookie);

    /// <summary>Serves the menu entry until Windows has not asked for a minute.</summary>
    public static void RunServer()
    {
        var clsid = new Guid(Clsid);
        int hr = CoRegisterClassObject(ref clsid, new Factory(), 4 /* local server */, 1 /* multiple use */, out uint cookie);
        if (hr != 0) { Log.Write($"menu: CoRegisterClassObject 0x{hr:X8}"); return; }
        var dispatcher = Dispatcher.CurrentDispatcher;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        timer.Tick += (_, _) => { if ((DateTime.Now - lastUse).TotalSeconds > 60) dispatcher.InvokeShutdown(); };
        timer.Start();
        Dispatcher.Run();
        CoRevokeClassObject(cookie);
    }

    internal static bool Supported(string path) =>
        Installer.Types.Contains(Path.GetExtension(path ?? "").ToLowerInvariant());

    internal static List<string> Paths(IShellItemArray items)
    {
        var list = new List<string>();
        try
        {
            if (items == null) return list;
            items.GetCount(out uint n);
            for (uint i = 0; i < n; i++)
            {
                items.GetItemAt(i, out IShellItem item);
                item.GetDisplayName(0x80058000 /* file system path */, out string path);
                if (!string.IsNullOrEmpty(path)) list.Add(path);
            }
        }
        catch (Exception e) { Log.Write("menu items: " + e.Message); }
        return list;
    }

    /// <summary>
    /// Starts the normal app with the files. It is started "outside" the menu package,
    /// so it runs exactly like when opened from Open with (same settings and AI folder).
    /// </summary>
    internal static void Launch(List<string> paths)
    {
        string exe = Environment.ProcessPath;
        AllowSetForegroundWindow(-1);      // let the window come to the front (otherwise Windows only flashes it orange in the taskbar)
        var cmd = new StringBuilder($"\"{exe}\"");
        foreach (var p in paths) cmd.Append(" \"").Append(p).Append('"');
        // the small menu package: start outside it. The Store version stays inside its package (its settings and AI live there).
        if (!Program.IsStore && StartOutsidePackage(exe, cmd.ToString())) return;
        try
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
            foreach (var p in paths) psi.ArgumentList.Add(p);
            Process.Start(psi);
        }
        catch (Exception e) { Log.Write("menu launch: " + e.Message); }
    }

    [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int processId);

    // --- CreateProcess with "break away from the package" ---
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO
    {
        public int cb;
        public string lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr prev, IntPtr returnSize);
    [DllImport("kernel32.dll")] static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateProcess(string app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags,
                                     IntPtr env, string dir, ref STARTUPINFOEX si, out PROCESS_INFORMATION pi);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

    static bool StartOutsidePackage(string exe, string commandLine)
    {
        IntPtr list = IntPtr.Zero, value = IntPtr.Zero;
        try
        {
            IntPtr size = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            list = Marshal.AllocHGlobal(size);
            if (!InitializeProcThreadAttributeList(list, 1, 0, ref size)) return false;
            value = Marshal.AllocHGlobal(4);
            Marshal.WriteInt32(value, 0x01);                                   // break away, with its children
            if (!UpdateProcThreadAttribute(list, 0, (IntPtr)0x00020012, value, (IntPtr)4, IntPtr.Zero, IntPtr.Zero)) return false;
            var si = new STARTUPINFOEX { lpAttributeList = list };
            si.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
            bool ok = CreateProcess(exe, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, false, 0x00080000 /* extended startup info */,
                                    IntPtr.Zero, Path.GetDirectoryName(exe), ref si, out var pi);
            if (ok) { CloseHandle(pi.hProcess); CloseHandle(pi.hThread); }
            else Log.Write("menu: CreateProcess error " + Marshal.GetLastWin32Error());
            return ok;
        }
        catch (Exception e)
        {
            Log.Write("menu breakaway: " + e.Message);
            return false;
        }
        finally
        {
            if (list != IntPtr.Zero) { DeleteProcThreadAttributeList(list); Marshal.FreeHGlobal(list); }
            if (value != IntPtr.Zero) Marshal.FreeHGlobal(value);
        }
    }
}

// ---------------- COM pieces ----------------
[ComImport, Guid("00000001-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IClassFactory
{
    [PreserveSig] int CreateInstance(IntPtr outer, ref Guid riid, out IntPtr ppv);
    [PreserveSig] int LockServer([MarshalAs(UnmanagedType.Bool)] bool fLock);
}

[ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IShellItem
{
    void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
    void GetParent(out IShellItem ppsi);
    void GetDisplayName(uint sigdn, [MarshalAs(UnmanagedType.LPWStr)] out string name);
    void GetAttributes(uint mask, out uint attributes);
    void Compare(IShellItem other, uint hint, out int order);
}

[ComImport, Guid("b63ea76d-1f85-456f-a19c-48159efa858b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IShellItemArray
{
    void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
    void GetPropertyStore(int flags, ref Guid riid, out IntPtr ppv);
    void GetPropertyDescriptionList(IntPtr keyType, ref Guid riid, out IntPtr ppv);
    void GetAttributes(int attribFlags, uint mask, out uint attributes);
    void GetCount(out uint count);
    void GetItemAt(uint index, out IShellItem item);
    void EnumItems(out IntPtr items);
}

[ComImport, Guid("a08ce4d0-fa25-44ab-b57c-c7b1c323e0b9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IExplorerCommand
{
    [PreserveSig] int GetTitle(IShellItemArray items, [MarshalAs(UnmanagedType.LPWStr)] out string name);
    [PreserveSig] int GetIcon(IShellItemArray items, [MarshalAs(UnmanagedType.LPWStr)] out string icon);
    [PreserveSig] int GetToolTip(IShellItemArray items, [MarshalAs(UnmanagedType.LPWStr)] out string tip);
    [PreserveSig] int GetCanonicalName(out Guid guid);
    [PreserveSig] int GetState(IShellItemArray items, [MarshalAs(UnmanagedType.Bool)] bool okToBeSlow, out uint state);
    [PreserveSig] int Invoke(IShellItemArray items, IntPtr bindContext);
    [PreserveSig] int GetFlags(out uint flags);
    [PreserveSig] int EnumSubCommands(out IntPtr commands);
}

[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
class Factory : IClassFactory
{
    static readonly Guid IUnknown = new("00000000-0000-0000-C000-000000000046");
    static readonly Guid IExplorerCommandId = new("a08ce4d0-fa25-44ab-b57c-c7b1c323e0b9");

    public int CreateInstance(IntPtr outer, ref Guid riid, out IntPtr ppv)
    {
        ppv = IntPtr.Zero;
        if (outer != IntPtr.Zero) return unchecked((int)0x80040110);          // no aggregation
        MenuCommand.Touch();
        var command = new ConvertCommand();
        if (riid == IExplorerCommandId) ppv = Marshal.GetComInterfaceForObject(command, typeof(IExplorerCommand));
        else if (riid == IUnknown) ppv = Marshal.GetIUnknownForObject(command);
        else return unchecked((int)0x80004002);                                 // no such interface
        return 0;
    }

    public int LockServer(bool fLock) => 0;
}

[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
class ConvertCommand : IExplorerCommand
{
    const int NotImplemented = unchecked((int)0x80004001);

    public int GetTitle(IShellItemArray items, out string name) { name = "Convert to Markdown"; return 0; }

    public int GetIcon(IShellItemArray items, out string icon) { icon = Environment.ProcessPath + ",0"; return 0; }

    public int GetToolTip(IShellItemArray items, out string tip) { tip = null; return NotImplemented; }

    public int GetCanonicalName(out Guid guid) { guid = new Guid(MenuCommand.Clsid); return 0; }

    public int GetState(IShellItemArray items, bool okToBeSlow, out uint state)
    {
        MenuCommand.Touch();
        var paths = MenuCommand.Paths(items);
        state = paths.Count > 0 && paths.All(MenuCommand.Supported) ? 0u /* shown */ : 2u /* hidden */;
        return 0;
    }

    public int Invoke(IShellItemArray items, IntPtr bindContext)
    {
        MenuCommand.Touch();
        var paths = MenuCommand.Paths(items).Where(MenuCommand.Supported).ToList();
        if (paths.Count > 0) MenuCommand.Launch(paths);
        return 0;
    }

    public int GetFlags(out uint flags) { flags = 0; return 0; }

    public int EnumSubCommands(out IntPtr commands) { commands = IntPtr.Zero; return NotImplemented; }
}
