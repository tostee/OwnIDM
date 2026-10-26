using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace OwnIDM.App;

/// <summary>
/// Opens magnet links and .torrent files with OwnIDM (per user, no admin rights). Windows keeps the
/// default app the user picked in a protected "UserChoice" key that no program may write: when another
/// app was chosen there, OwnIDM can only offer itself in Settings → Default apps.
/// </summary>
public static class TorrentAssociation
{
    private const string AppName = "OwnIDM";
    private const string TorrentProgId = "OwnIDM.Torrent";
    private const string MagnetProgId = "OwnIDM.Magnet";
    private const string CapabilitiesKey = @"Software\OwnIDM\Capabilities";

    private static string Exe => Environment.ProcessPath!;
    private static string Command => $"\"{Exe}\" \"%1\"";

    /// <summary>True when Windows opens both .torrent files and magnet links with this OwnIDM.</summary>
    public static bool IsDefault => OpensWithUs(".torrent", isProtocol: false) && OpensWithUs("magnet", isProtocol: true);

    public static void Register()
    {
        RememberPreviousHandlers();
        WriteHandler($@"Software\Classes\{TorrentProgId}", "Archivo torrent", protocol: false);
        WriteHandler($@"Software\Classes\{MagnetProgId}", "URL:Magnet link", protocol: true);

        // The per-user handlers, used unless the user picked another app in Windows.
        using (var ext = Registry.CurrentUser.CreateSubKey(@"Software\Classes\.torrent"))
        {
            ext.SetValue("", TorrentProgId);
            ext.SetValue("Content Type", "application/x-bittorrent");
            using var openWith = ext.CreateSubKey("OpenWithProgids");
            openWith.SetValue(TorrentProgId, Array.Empty<byte>(), RegistryValueKind.None);
        }
        WriteHandler(@"Software\Classes\magnet", "URL:Magnet link", protocol: true);

        // Listed in Settings → Default apps, so the user can choose OwnIDM there.
        using (var caps = Registry.CurrentUser.CreateSubKey(CapabilitiesKey))
        {
            caps.SetValue("ApplicationName", AppName);
            caps.SetValue("ApplicationDescription", "Gestor de descargas con soporte de torrents");
            using (var files = caps.CreateSubKey("FileAssociations"))
                files.SetValue(".torrent", TorrentProgId);
            using (var urls = caps.CreateSubKey("UrlAssociations"))
                urls.SetValue("magnet", MagnetProgId);
        }
        using (var apps = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications"))
            apps.SetValue(AppName, CapabilitiesKey);

        SHChangeNotify(SHCNE_ASSOCCHANGED, 0, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>
    /// Removes what <see cref="Register"/> wrote and gives back the per-user handlers it replaced (another
    /// torrent client's); handlers that are not OwnIDM's are left alone.
    /// </summary>
    public static void Unregister()
    {
        var classes = @"Software\Classes\";
        string? previousTorrent, previousMagnet;
        using (var previous = Registry.CurrentUser.OpenSubKey(PreviousKey))
        {
            previousTorrent = previous?.GetValue(".torrent") as string;
            previousMagnet = previous?.GetValue("magnet") as string;
        }
        Registry.CurrentUser.DeleteSubKeyTree(classes + TorrentProgId, throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree(classes + MagnetProgId, throwOnMissingSubKey: false);
        using (var ext = Registry.CurrentUser.OpenSubKey(classes + ".torrent", writable: true))
        {
            if (ext?.GetValue("") as string == TorrentProgId)
            {
                if (previousTorrent is not null)
                    ext.SetValue("", previousTorrent);
                else
                    ext.DeleteValue("", throwOnMissingValue: false);
            }
            using var openWith = ext?.OpenSubKey("OpenWithProgids", writable: true);
            openWith?.DeleteValue(TorrentProgId, throwOnMissingValue: false);
        }
        string? magnetCommand;
        using (var command = Registry.CurrentUser.OpenSubKey(classes + @"magnet\shell\open\command"))
            magnetCommand = command?.GetValue("") as string;
        if (magnetCommand?.Contains(Exe, StringComparison.OrdinalIgnoreCase) == true)
        {
            if (previousMagnet is not null)
            {
                using var command = Registry.CurrentUser.CreateSubKey(classes + @"magnet\shell\open\command");
                command.SetValue("", previousMagnet);
                Registry.CurrentUser.DeleteSubKeyTree(classes + @"magnet\DefaultIcon", throwOnMissingSubKey: false);
            }
            else
            {
                Registry.CurrentUser.DeleteSubKeyTree(classes + "magnet", throwOnMissingSubKey: false);
            }
        }
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\OwnIDM\Capabilities", throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree(PreviousKey, throwOnMissingSubKey: false);
        using (var apps = Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications", writable: true))
            apps?.DeleteValue(AppName, throwOnMissingValue: false);
        SHChangeNotify(SHCNE_ASSOCCHANGED, 0, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>Opens Settings → Default apps at OwnIDM's page (Windows 11; the general page on Windows 10).</summary>
    public static void OpenDefaultAppsSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:defaultapps?registeredAppUser=" + AppName) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Process.Start(new ProcessStartInfo("ms-settings:defaultapps") { UseShellExecute = true });
        }
    }

    private const string PreviousKey = @"Software\OwnIDM\PreviousHandlers";

    /// <summary>Keeps the per-user handlers about to be replaced, the first time only, for <see cref="Unregister"/>.</summary>
    private static void RememberPreviousHandlers()
    {
        using var previous = Registry.CurrentUser.CreateSubKey(PreviousKey);
        if (previous.GetValue(".torrent") is null)
        {
            using var ext = Registry.CurrentUser.OpenSubKey(@"Software\Classes\.torrent");
            if (ext?.GetValue("") is string progId && progId.Length > 0 && progId != TorrentProgId)
                previous.SetValue(".torrent", progId);
        }
        if (previous.GetValue("magnet") is null)
        {
            using var command = Registry.CurrentUser.OpenSubKey(@"Software\Classes\magnet\shell\open\command");
            if (command?.GetValue("") is string value && value.Length > 0 && !value.Contains(Exe, StringComparison.OrdinalIgnoreCase))
                previous.SetValue("magnet", value);
        }
    }

    private static void WriteHandler(string key, string description, bool protocol)
    {
        using var handler = Registry.CurrentUser.CreateSubKey(key);
        handler.SetValue("", description);
        if (protocol)
            handler.SetValue("URL Protocol", "");
        using (var icon = handler.CreateSubKey("DefaultIcon"))
            icon.SetValue("", $"\"{Exe}\",0");
        using var command = handler.CreateSubKey(@"shell\open\command");
        command.SetValue("", Command);
    }

    /// <summary>The command Windows really runs (it takes UserChoice into account) starts this OwnIDM.</summary>
    private static bool OpensWithUs(string assoc, bool isProtocol)
    {
        uint size = 1024;
        var buffer = new StringBuilder((int)size);
        int hr = AssocQueryString(isProtocol ? ASSOCF_IS_PROTOCOL : 0, ASSOCSTR_COMMAND, assoc, "open", buffer, ref size);
        return hr == 0 && buffer.ToString().Contains(Exe, StringComparison.OrdinalIgnoreCase);
    }

    private const uint ASSOCF_IS_PROTOCOL = 0x00001000;
    private const int ASSOCSTR_COMMAND = 1;
    private const int SHCNE_ASSOCCHANGED = 0x08000000;

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int AssocQueryString(uint flags, int str, string assoc, string? extra, StringBuilder? output, ref uint size);

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
}
