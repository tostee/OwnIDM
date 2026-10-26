using System.Text.Json;
using Microsoft.Win32;

namespace OwnIDM.App;

/// <summary>Registers the native messaging host for Chromium based browsers (per user, no admin).</summary>
public static class BrowserIntegration
{
    public const string HostName = "com.ownidm.host";

    /// <summary>Derived from the "key" in extension/manifest.json, so it is stable for unpacked installs.</summary>
    public const string ExtensionId = "jnnkhifhlmmlofgelokpmjcgpflmgpld";

    private static readonly string[] RegistryRoots =
    [
        @"Software\Google\Chrome\NativeMessagingHosts",
        @"Software\Microsoft\Edge\NativeMessagingHosts",
        @"Software\Chromium\NativeMessagingHosts",
        @"Software\BraveSoftware\Brave-Browser\NativeMessagingHosts",
        @"Software\Vivaldi\NativeMessagingHosts",
    ];

    public static string HostExe => Path.Combine(AppContext.BaseDirectory, "OwnIDM.NativeHost.exe");
    public static string ExtensionFolder => Path.Combine(AppContext.BaseDirectory, "extension");

    private static string ManifestPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OwnIDM", HostName + ".json");

    public static bool TryRegister(out string? error)
    {
        try
        {
            if (!File.Exists(HostExe))
            {
                error = "No se encontró OwnIDM.NativeHost.exe junto a OwnIDM.exe.";
                return false;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(ManifestPath)!);
            var manifest = new Dictionary<string, object>
            {
                ["name"] = HostName,
                ["description"] = "OwnIDM browser integration host",
                ["path"] = HostExe,
                ["type"] = "stdio",
                ["allowed_origins"] = new[] { $"chrome-extension://{ExtensionId}/" },
            };
            // Runs at every start: only write what changed. Rewriting browser registry keys each
            // time is the kind of behaviour antivirus heuristics flag.
            var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
            if (!File.Exists(ManifestPath) || File.ReadAllText(ManifestPath) != json)
                File.WriteAllText(ManifestPath, json);
            foreach (var root in RegistryRoots)
            {
                using (var existing = Registry.CurrentUser.OpenSubKey($@"{root}\{HostName}"))
                    if (existing?.GetValue("") as string == ManifestPath)
                        continue;
                using var key = Registry.CurrentUser.CreateSubKey($@"{root}\{HostName}");
                key.SetValue("", ManifestPath);
            }
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            error = ex.Message;
            return false;
        }
    }

    public static void Unregister()
    {
        foreach (var root in RegistryRoots)
            Registry.CurrentUser.DeleteSubKeyTree($@"{root}\{HostName}", throwOnMissingSubKey: false);
        if (File.Exists(ManifestPath))
            File.Delete(ManifestPath);
    }

    public static bool IsRegistered
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"{RegistryRoots[0]}\{HostName}");
            return key?.GetValue("") is string path && File.Exists(path);
        }
    }
}

/// <summary>"Launch on Windows startup" through the per-user Run key.</summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "OwnIDM";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
            key.SetValue(ValueName, $"\"{Environment.ProcessPath}\" --tray");
        else
            key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
