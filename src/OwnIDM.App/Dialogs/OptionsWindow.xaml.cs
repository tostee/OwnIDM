using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using Microsoft.Win32;
using OwnIDM.Core.Hls;
using OwnIDM.Core.Manager;
using OwnIDM.Core.Settings;
using OwnIDM.Core.Tools;
using OwnIDM.Core.YtDlp;

namespace OwnIDM.App;

public partial class OptionsWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly DownloadManager _manager;
    private readonly ObservableCollection<Category> _categories;

    public OptionsWindow(DownloadManager manager)
    {
        InitializeComponent();
        _manager = manager;
        var s = manager.Settings;

        StartupBox.IsChecked = StartupRegistration.IsEnabled;
        ClipboardBox.IsChecked = s.MonitorClipboard;
        MonitoredBox.Text = s.MonitoredExtensions;
        ProgressBox.IsChecked = s.ShowProgressWindow;
        CompleteBox.IsChecked = s.ShowCompleteDialog;
        UpdatesBox.IsChecked = s.CheckForUpdates;

        ConnectionsBox.ItemsSource = new[] { 1, 2, 4, 8, 16, 24, 32 };
        ConnectionsBox.SelectedItem = s.MaxConnectionsPerDownload;
        if (ConnectionsBox.SelectedItem is null)
            ConnectionsBox.SelectedItem = 8;
        ConcurrentBox.Text = s.MaxConcurrentDownloads.ToString();
        RetriesBox.Text = s.MaxRetries.ToString();
        TimeoutBox.Text = s.TimeoutSeconds.ToString();

        DownloadDirBox.Text = s.DefaultDownloadDirectory;
        TempDirBox.Text = s.TempDirectory;
        _categories = new ObservableCollection<Category>(s.Categories.Select(c =>
            new Category { Name = c.Name, Extensions = c.Extensions, Directory = c.Directory }));
        CategoriesGrid.ItemsSource = _categories;

        LimiterBox.IsChecked = s.SpeedLimiterEnabled;
        LimitBox.Text = s.SpeedLimitKBps.ToString();

        BrowserBox.IsChecked = s.BrowserIntegration;
        ConvertBox.IsChecked = s.ConvertHlsToMp4;
        FfmpegBox.Text = s.FfmpegPath;
        YtDlpBox.Text = s.YtDlpPath;
        TorrentPortBox.Text = s.TorrentPort.ToString();
        TorrentConnectionsBox.Text = s.TorrentMaxConnections.ToString();
        TorrentUploadBox.Text = s.TorrentUploadLimitKBps.ToString();
        TorrentDhtBox.IsChecked = s.TorrentDht;
        TorrentForwardBox.IsChecked = s.TorrentPortForwarding;
        RefreshHostStatus();
        RefreshToolsStatus();
        RefreshTorrentAssociation();
    }

    private void RefreshTorrentAssociation() =>
        TorrentAssocStatus.Text = TorrentAssociation.IsDefault ? "se abren con OwnIDM ✔" : "se abren con otro programa";

    private void OnAssociateTorrents(object sender, RoutedEventArgs e)
    {
        try
        {
            TorrentAssociation.Register();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            MessageBox.Show(this, "No se pudo actualizar el registro: " + ex.Message, "Torrent", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        RefreshTorrentAssociation();
        if (TorrentAssociation.IsDefault)
            return;
        // Windows keeps the user's choice of default app protected: it must be changed in Settings.
        MessageBox.Show(this,
            "Windows tiene elegido otro programa para los torrents (por ejemplo uTorrent) y solo tú puedes cambiarlo.\n\n" +
            "Se abrirá «Aplicaciones predeterminadas»: elige OwnIDM para «.torrent» y para «MAGNET».",
            "Torrent", MessageBoxButton.OK, MessageBoxImage.Information);
        TorrentAssociation.OpenDefaultAppsSettings();
    }

    private void RefreshHostStatus() =>
        HostStatus.Text = BrowserIntegration.IsRegistered ? "registrado ✔" : "no registrado";

    private static bool TryInt(string text, int min, int max, out int value) =>
        int.TryParse(text.Trim(), out value) && value >= min && value <= max;

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (!TryInt(ConcurrentBox.Text, 1, 20, out int concurrent) ||
            !TryInt(RetriesBox.Text, 0, 100, out int retries) ||
            !TryInt(TimeoutBox.Text, 5, 600, out int timeout) ||
            !TryInt(LimitBox.Text, 1, 10_000_000, out int limit) ||
            !TryInt(TorrentPortBox.Text, 1024, 65535, out int torrentPort) ||
            !TryInt(TorrentConnectionsBox.Text, 5, 500, out int torrentConnections) ||
            !TryInt(TorrentUploadBox.Text, 0, 10_000_000, out int torrentUpload))
        {
            MessageBox.Show(this, "Revisa los valores numéricos (descargas simultáneas 1-20, reintentos 0-100, " +
                                  "tiempo de espera 5-600 s, límite ≥ 1 KB/s, puerto 1024-65535, conexiones por torrent 5-500).",
                "Opciones", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (string.IsNullOrWhiteSpace(DownloadDirBox.Text) || string.IsNullOrWhiteSpace(TempDirBox.Text))
        {
            MessageBox.Show(this, "Las carpetas no pueden estar vacías.", "Opciones", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var s = _manager.Settings;
        s.MonitorClipboard = ClipboardBox.IsChecked == true;
        s.MonitoredExtensions = MonitoredBox.Text.Trim();
        s.ShowProgressWindow = ProgressBox.IsChecked == true;
        s.ShowCompleteDialog = CompleteBox.IsChecked == true;
        s.CheckForUpdates = UpdatesBox.IsChecked == true;
        s.MaxConnectionsPerDownload = ConnectionsBox.SelectedItem is int c ? c : 8;
        s.MaxConcurrentDownloads = concurrent;
        s.MaxRetries = retries;
        s.TimeoutSeconds = timeout;
        s.DefaultDownloadDirectory = DownloadDirBox.Text.Trim();
        s.TempDirectory = TempDirBox.Text.Trim();
        s.SpeedLimiterEnabled = LimiterBox.IsChecked == true;
        s.SpeedLimitKBps = limit;
        s.BrowserIntegration = BrowserBox.IsChecked == true;
        s.ConvertHlsToMp4 = ConvertBox.IsChecked == true;
        s.FfmpegPath = FfmpegBox.Text.Trim();
        s.YtDlpPath = YtDlpBox.Text.Trim();
        bool torrentEngineChanged = s.TorrentPort != torrentPort || s.TorrentDht != (TorrentDhtBox.IsChecked == true) ||
                                    s.TorrentPortForwarding != (TorrentForwardBox.IsChecked == true);
        s.TorrentPort = torrentPort;
        s.TorrentMaxConnections = torrentConnections;
        s.TorrentUploadLimitKBps = torrentUpload;
        s.TorrentDht = TorrentDhtBox.IsChecked == true;
        s.TorrentPortForwarding = TorrentForwardBox.IsChecked == true;
        if (torrentEngineChanged)
            _ = ApplyTorrentSettingsAsync();

        var categories = _categories.Where(x => !string.IsNullOrWhiteSpace(x.Name)).ToList();
        if (!categories.Any(x => x.Name == AppSettings.GeneralCategory))
            categories.Insert(0, new Category { Name = AppSettings.GeneralCategory });
        s.Categories = categories;

        try
        {
            StartupRegistration.Set(StartupBox.IsChecked == true);
            if (s.BrowserIntegration)
                BrowserIntegration.TryRegister(out _);
            else
                BrowserIntegration.Unregister();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            MessageBox.Show(this, "No se pudo actualizar el registro: " + ex.Message, "Opciones", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        _manager.SaveSettings();
        DialogResult = true;
    }

    /// <summary>A running torrent engine takes the new port and DHT setting without a restart.</summary>
    private async Task ApplyTorrentSettingsAsync()
    {
        try
        {
            await _manager.Torrents.ApplySettingsAsync();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Net.Sockets.SocketException)
        {
            MessageBox.Show("No se pudieron aplicar los ajustes de torrent: " + ex.Message + "\nSe aplicarán al reiniciar OwnIDM.",
                "Torrent", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnBrowseDownloadDir(object sender, RoutedEventArgs e) => Browse(DownloadDirBox);

    private void OnBrowseTempDir(object sender, RoutedEventArgs e) => Browse(TempDirBox);

    private void Browse(System.Windows.Controls.TextBox box)
    {
        var dialog = new OpenFolderDialog { InitialDirectory = Directory.Exists(box.Text) ? box.Text : null };
        if (dialog.ShowDialog(this) == true)
            box.Text = dialog.FolderName;
    }

    private void OnFfmpegChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        var probe = new AppSettings { FfmpegPath = FfmpegBox.Text.Trim(), ToolsDirectory = _manager.Settings.ToolsDirectory };
        var found = Ffmpeg.Find(probe);
        FfmpegStatus.Text = found is null
            ? "ffmpeg no encontrado: los vídeos HLS se guardarán como .ts (se reproducen en VLC y la mayoría de reproductores)."
            : "ffmpeg encontrado: " + found;
        FfmpegStatus.SetResourceReference(ForegroundProperty, found is null ? "SystemFillColorCautionBrush" : "SystemFillColorSuccessBrush");
    }

    private void OnYtDlpChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        var probe = new AppSettings { YtDlpPath = YtDlpBox.Text.Trim(), ToolsDirectory = _manager.Settings.ToolsDirectory };
        var found = YtDlpTool.Find(probe);
        bool js = YtDlpTool.FindJsRuntime(probe) is not null;
        YtDlpStatus.Text = found is null
            ? "yt-dlp no encontrado: instálalo con «Instalar o actualizar componentes»."
            : "yt-dlp: " + found + (js ? "" : "\nNo hay Deno ni Node.js: YouTube puede ofrecer pocas calidades o fallar.");
        YtDlpStatus.SetResourceReference(ForegroundProperty, found is null || !js ? "SystemFillColorCautionBrush" : "SystemFillColorSuccessBrush");
    }

    /// <summary>One line per component: OwnIDM's own copy (with version), one found elsewhere, or missing.</summary>
    private void RefreshToolsStatus()
    {
        var s = _manager.Settings;
        string Line(Tool tool, string? found)
        {
            var name = ToolManager.Display(tool);
            if (_manager.Tools.InstalledVersion(tool) is { } version)
                return $"✔ {name} {version} (instalado por OwnIDM)";
            return found is null ? $"✘ {name}: no instalado" : $"✔ {name}: {found}";
        }
        ToolsStatus.Text = string.Join("\n",
            Line(Tool.YtDlp, YtDlpTool.Find(s)),
            Line(Tool.Ffmpeg, Ffmpeg.Find(s)),
            Line(Tool.Deno, YtDlpTool.FindJsRuntime(s)));
    }

    private void OnInstallComponents(object sender, RoutedEventArgs e)
    {
        var dialog = new ComponentsWindow(_manager, [Tool.YtDlp, Tool.Ffmpeg, Tool.Deno],
            "OwnIDM descargará la última versión de estos componentes:") { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            RefreshToolsStatus();
            OnFfmpegChanged(this, null!);
            OnYtDlpChanged(this, null!);
        }
    }

    private void OnBrowseYtDlp(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "yt-dlp.exe|yt-dlp.exe|Programas (*.exe)|*.exe" };
        if (dialog.ShowDialog(this) == true)
            YtDlpBox.Text = dialog.FileName;
    }

    private void OnBrowseFfmpeg(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "ffmpeg.exe|ffmpeg.exe|Programas (*.exe)|*.exe" };
        if (dialog.ShowDialog(this) == true)
            FfmpegBox.Text = dialog.FileName;
    }

    private void OnRegisterHost(object sender, RoutedEventArgs e)
    {
        if (!BrowserIntegration.TryRegister(out var error))
            MessageBox.Show(this, "No se pudo registrar: " + error, "Navegador", MessageBoxButton.OK, MessageBoxImage.Warning);
        RefreshHostStatus();
    }

    private void OnOpenExtensionFolder(object sender, RoutedEventArgs e)
    {
        if (Directory.Exists(BrowserIntegration.ExtensionFolder))
            Process.Start("explorer.exe", $"\"{BrowserIntegration.ExtensionFolder}\"");
    }

    private void OnCopyExtensionPath(object sender, RoutedEventArgs e) =>
        Clipboard.SetText(BrowserIntegration.ExtensionFolder);
}
