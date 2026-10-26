using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Markup;
using OwnIDM.Core.Ipc;
using OwnIDM.Core.Manager;
using OwnIDM.Core.Torrents;
using OwnIDM.Core.Update;

namespace OwnIDM.App;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private IpcServer? _ipc;

    public static DownloadManager Manager { get; private set; } = null!;

    public static AppUpdater Updater { get; private set; } = null!;

    /// <summary>Version of this build, from Directory.Build.props.</summary>
    public static Version Version => Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(1, 0, 0);

    public static readonly CultureInfo Spanish = CultureInfo.GetCultureInfo("es-ES");

    private const string UnregisterArg = "--unregister-associations";

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // The UI is in Spanish: dates, date pickers and bindings follow it whatever the Windows language.
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture = Spanish;
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = Spanish;
        FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement),
            new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(Spanish.Name)));
        if (e.Args.Contains(UnregisterArg))
        {
            // Run by the uninstaller.
            TorrentAssociation.Unregister();
            Shutdown();
            return;
        }
        // An address, or a magnet link / .torrent file opened from the browser or Explorer.
        var url = e.Args.FirstOrDefault(a => Uri.TryCreate(a, UriKind.Absolute, out var u) && u.Scheme.StartsWith("http") ||
                                             TorrentLink.IsMagnet(a) || TorrentLink.IsTorrentFile(a));
        bool startHidden = e.Args.Contains("--tray");

        _singleInstance = new Mutex(true, "OwnIDM.SingleInstance." + Environment.UserName, out bool isFirst);
        if (!isFirst)
        {
            await IpcClient.SendAsync(url is null ? new IpcMessage { Type = "show" } : new IpcMessage { Url = url },
                TimeSpan.FromSeconds(1));
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            LogError(args.Exception);
            MessageBox.Show(args.Exception.Message, "OwnIDM: error inesperado", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        Manager = new DownloadManager(new Persistence());
        var feed = Assembly.GetEntryAssembly()?.GetCustomAttributes<AssemblyMetadataAttribute>()
                           .FirstOrDefault(a => a.Key == "UpdateFeed")?.Value;
        Updater = new AppUpdater(Manager.Settings, Manager.Engine, feed, Version);
        Updater.Cleanup(); // the installer of an update already applied
        if (Manager.Settings.BrowserIntegration)
            BrowserIntegration.TryRegister(out _);

        var window = new MainWindow();
        MainWindow = window;
        if (!startHidden)
            window.Show();

        _ipc = new IpcServer(msg => Dispatcher.InvokeAsync(() => window.HandleIpc(msg)).Task.Unwrap());
        _ipc.Start();
        // Keeps OwnIDM's yt-dlp current; like the OwnIDM update check, only if the user allows it.
        _ = Task.Run(() => Manager.Tools.MaintainAsync(Manager.Settings.CheckForUpdates));
        _ = window.CheckForUpdatesAsync(manual: false);

        if (url is not null)
            _ = window.HandleIpc(new IpcMessage { Url = url });
    }

    private static void LogError(Exception ex)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OwnIDM");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "error.log"), $"[{DateTime.Now:O}] {ex}{Environment.NewLine}");
        }
        catch (IOException) { }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _ipc?.Dispose();
        Updater?.Dispose();
        Manager?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
