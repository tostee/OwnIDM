using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Markup;
using OwnIDM.Core.Ipc;
using OwnIDM.Core.Manager;
using OwnIDM.Core.Torrents;
using OwnIDM.Core.Update;
using Wpf.Ui.Appearance;

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
        var failedUpdateLog = Updater.TakeFailedInstallLog();
        Updater.Cleanup(); // the installer of an update already applied
        if (Manager.Settings.BrowserIntegration)
            BrowserIntegration.TryRegister(out _);

        FixedSizeDialogs.Register();

        // Light or dark like Windows, always with OwnIDM's blue as the accent (not the Windows accent color).
        ApplicationThemeManager.Changed += (theme, _) => ApplyAccent(theme);
        ApplicationThemeManager.ApplySystemTheme();
        ApplyAccent(ApplicationThemeManager.GetAppTheme());

        var window = new MainWindow();
        SystemThemeWatcher.Watch(window, Wpf.Ui.Controls.WindowBackdropType.Mica, updateAccents: false);
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
        if (failedUpdateLog is not null)
            Dispatcher.BeginInvoke(() => ReportFailedUpdate(window, failedUpdateLog), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    /// <summary>The installer of an update failed and undid its changes; this is the old version, reopened by it.</summary>
    private static void ReportFailedUpdate(Window owner, string log)
    {
        var text = "La actualización de OwnIDM no se pudo instalar y se deshicieron los cambios; sigues con la versión " +
                   $"{Updater.CurrentVersion}.\n\nSuele deberse a un archivo de OwnIDM bloqueado (por el antivirus o por un " +
                   "proceso que seguía abierto). Vuelve a intentarlo desde Ayuda > Buscar actualizaciones";
        if (log.Length == 0)
        {
            MessageBox.Show(owner, text + ".", "Actualización de OwnIDM",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (MessageBox.Show(owner, text + $"; si vuelve a fallar, envía este registro:\n{log}\n\n¿Abrir la carpeta del registro?",
                "Actualización de OwnIDM", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
        {
            try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{log}\""); }
            catch (System.ComponentModel.Win32Exception) { }
        }
    }

    private static void ApplyAccent(ApplicationTheme theme) =>
        ApplicationAccentColorManager.Apply(System.Windows.Media.Color.FromRgb(0x1F, 0x6F, 0xD8), theme);

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
