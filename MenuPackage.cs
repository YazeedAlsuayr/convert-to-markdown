// Registers the small signed package (made by MakeMenuPackage.ps1 and stored inside the app)
// that puts "Convert to Markdown" at the top of the Windows 11 right-click menu.

using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;
using Windows.Management.Deployment;

namespace ConvertToMarkdown;

static class MenuPackage
{
    const string Name = "ConvertToMarkdown.Menu";

    static Stream Resource(string name) => typeof(MenuPackage).Assembly.GetManifestResourceStream(name);

    /// <summary>True when Build.cmd could make the package (Windows SDK tools were found).</summary>
    public static bool Available
    {
        get
        {
            using var a = Resource("menu.msix");
            using var b = Resource("menu.cer");
            return a != null && b != null;
        }
    }

    /// <summary>Installs the menu entry. Returns null when it worked, otherwise what went wrong.</summary>
    public static string Install()
    {
        try
        {
            string dir = Path.Combine(Installer.Dir, "menu");
            Directory.CreateDirectory(dir);
            string msix = Path.Combine(dir, "ConvertToMarkdown.Menu.msix");
            string cer = Path.Combine(dir, "ConvertToMarkdown.cer");
            Save("menu.msix", msix);
            Save("menu.cer", cer);

            // 1. Windows must trust the package's signature: needs administrator permission once
            using var cert = new X509Certificate2(cer);
            if (!IsTrusted(cert))
            {
                try
                {
                    var p = Process.Start(new ProcessStartInfo(Installer.Exe, $"--trust-cert \"{cer}\"") { UseShellExecute = true, Verb = "runas" });
                    p?.WaitForExit();
                }
                catch (Win32Exception)
                {
                    return "The entry at the top of the right-click menu was skipped because administrator permission was not given. " +
                           "Open with and Send to still work. Run Setup again to add it.";
                }
                if (!IsTrusted(cert))
                    return "The entry at the top of the right-click menu was skipped: Windows did not accept its signature.";
            }

            // 2. register the package, pointing at the installed app folder
            var pm = new PackageManager();
            var options = new AddPackageOptions
            {
                ExternalLocationUri = new Uri(Installer.Dir),
                ForceUpdateFromAnyVersion = true
            };
            var result = pm.AddPackageByUriAsync(new Uri(msix), options).AsTask().GetAwaiter().GetResult();
            if (result.ExtendedErrorCode != null)
            {
                Log.Write("menu package: " + result.ErrorText + " " + result.ExtendedErrorCode);
                return "The entry at the top of the right-click menu could not be added: " + result.ErrorText;
            }
            return null;
        }
        catch (Exception e)
        {
            Log.Write("menu package: " + e);
            return "The entry at the top of the right-click menu could not be added: " + e.Message;
        }
    }

    static void Save(string resource, string path)
    {
        using var s = Resource(resource);
        using var f = File.Create(path);
        s.CopyTo(f);
    }

    static bool IsTrusted(X509Certificate2 cert)
    {
        using var store = new X509Store(StoreName.TrustedPeople, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly);
        return store.Certificates.Find(X509FindType.FindByThumbprint, cert.Thumbprint, false).Count > 0;
    }

    /// <summary>Runs as administrator (--trust-cert): lets Windows trust the package's signature.</summary>
    public static int TrustCertificate(string cerPath)
    {
        try
        {
            using var cert = new X509Certificate2(cerPath);
            using var store = new X509Store(StoreName.TrustedPeople, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadWrite);
            store.Add(cert);
            return 0;
        }
        catch (Exception e)
        {
            Log.Write("trust certificate: " + e.Message);
            return 1;
        }
    }

    public static void Remove()
    {
        try
        {
            var pm = new PackageManager();
            foreach (var p in pm.FindPackagesForUser(string.Empty).Where(p => p.Id.Name == Name).ToList())
                pm.RemovePackageAsync(p.Id.FullName).AsTask().GetAwaiter().GetResult();
        }
        catch (Exception e) { Log.Write("remove menu package: " + e.Message); }
    }
}
