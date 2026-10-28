using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using OwnIDM.Core.Ipc;
using OwnIDM.Core.Manager;
using OwnIDM.Core.Models;
using OwnIDM.Core.Scheduling;
using OwnIDM.Core.Torrents;
using OwnIDM.Core.Update;
using OwnIDM.Core.Util;
using WinForms = System.Windows.Forms;

namespace OwnIDM.App;

public partial class MainWindow : Wpf.Ui.Controls.FluentWindow
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;

    private readonly DownloadManager _manager = App.Manager;
    private readonly ListCollectionView _view;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<Guid, ProgressWindow> _progressWindows = new();
    private readonly WinForms.NotifyIcon _tray;
    private Func<DownloadItem, bool> _filter = _ => true;
    private string? _lastClipboardUrl;
    private bool _ignoreNextClipboard;
    private bool _exiting;
    private CountdownWindow? _countdown;

    public MainWindow()
    {
        InitializeComponent();

        _view = (ListCollectionView)CollectionViewSource.GetDefaultView(_manager.Items);
        _view.Filter = o => o is DownloadItem d && _filter(d);
        _view.IsLiveFiltering = true;
        foreach (var p in new[] { nameof(DownloadItem.Status), nameof(DownloadItem.Category), nameof(DownloadItem.InQueue) })
            _view.LiveFilteringProperties.Add(p);
        _view.SortDescriptions.Add(new SortDescription(nameof(DownloadItem.AddedAt), ListSortDirection.Descending));
        DownloadsGrid.ItemsSource = _view;

        BuildCategoryTree();

        _manager.DownloadFinished += OnDownloadFinished;
        _manager.QueueStateChanged += UpdateToolbarState;
        _manager.QueueCompleted += OnQueueCompleted;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += (_, _) => OnTick();
        _timer.Start();

        _tray = CreateTrayIcon();
        UpdateToolbarState();
        OnTick();
    }

    // ---------------------------------------------------------------- categories

    private void BuildCategoryTree()
    {
        CategoryTree.Items.Clear();
        var all = Node("Todas las descargas", "", _ => true);
        foreach (var cat in _manager.Settings.Categories)
        {
            var name = cat.Name;
            all.Items.Add(Node(name, CategoryGlyphConverter.For(name), d => d.Category == name));
        }
        all.IsExpanded = true;
        all.IsSelected = true;
        CategoryTree.Items.Add(all);
        CategoryTree.Items.Add(Node("Sin terminar", "", d => d.Status != DownloadStatus.Completed));
        CategoryTree.Items.Add(Node("Terminadas", "", d => d.Status == DownloadStatus.Completed));
        CategoryTree.Items.Add(Node("Cola principal", "", d => d.InQueue));
    }

    private static TreeViewItem Node(string text, string glyph, Func<DownloadItem, bool> filter)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
        var icon = new TextBlock
        {
            Text = glyph,
            FontFamily = (System.Windows.Media.FontFamily)Application.Current.Resources["IconFont"],
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        icon.SetResourceReference(TextBlock.ForegroundProperty, "AccentTextFillColorPrimaryBrush");
        header.Children.Add(icon);
        header.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        return new TreeViewItem { Header = header, Tag = filter };
    }

    private void OnCategoryChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is TreeViewItem { Tag: Func<DownloadItem, bool> filter })
        {
            _filter = filter;
            _view.Refresh();
        }
    }

    private void OnToggleCategories(object sender, RoutedEventArgs e)
    {
        CategoryColumn.Width = HideCategoriesMenu.IsChecked ? new GridLength(0) : new GridLength(210);
    }

    // ---------------------------------------------------------------- periodic refresh

    private void OnTick()
    {
        switch (QueueScheduler.Evaluate(_manager.Settings.Schedule, DateTime.Now))
        {
            case ScheduledAction.StartQueue:
                _manager.StartQueue();
                _manager.SaveSettings();
                _tray.ShowBalloonTip(3000, "Programador", "La cola de descargas se ha iniciado.", WinForms.ToolTipIcon.Info);
                break;
            case ScheduledAction.StopQueue:
                if (_manager.QueueRunning)
                    _manager.StopQueue();
                _manager.SaveSettings();
                break;
        }
        _manager.Tick();
        int active = _manager.Items.Count(i => i.IsActive);
        double speed = _manager.Items.Where(i => i.IsActive).Sum(i => i.Speed);
        StatusText.Text = $"{_manager.Items.Count} descargas · {active} activas" +
                          (_manager.QueueRunning ? " · cola en marcha" : "") +
                          (_manager.Settings.SpeedLimiterEnabled ? $" · limitador {_manager.Settings.SpeedLimitKBps} KB/s por descarga" : "");
        TotalSpeedText.Text = speed > 0 ? "Velocidad total: " + Format.Speed(speed) : "";
        var tip = active > 0 ? $"OwnIDM - {active} descargando ({Format.Speed(speed)})" : "OwnIDM";
        _tray.Text = tip.Length > 63 ? tip[..63] : tip; // NotifyIcon limit
    }

    private void UpdateToolbarState()
    {
        if (_manager.QueueRunning)
            _countdown?.Close(); // the queue started again: nothing has finished
        QueueButton.Content = _manager.QueueRunning ? "Detener cola" : "Iniciar cola";
        LimiterMenu.IsChecked = _manager.Settings.SpeedLimiterEnabled;
        LimiterButton.Content = _manager.Settings.SpeedLimiterEnabled ? "Limitador: sí" : "Limitador";
    }

    // ---------------------------------------------------------------- adding downloads

    /// <summary>
    /// Entry point for the browser and second instances. Completes with true once OwnIDM has
    /// taken the download (the browser then cancels its own copy).
    /// </summary>
    public Task<bool> HandleIpc(IpcMessage msg)
    {
        if (msg.Type == "show")
        {
            ShowFromTray();
            return Task.FromResult(true);
        }
        if (msg.Type == "add" && !string.IsNullOrWhiteSpace(msg.Url))
            return OfferDownload(msg);
        return Task.FromResult(false);
    }

    private Task<bool> OfferDownload(IpcMessage msg)
    {
        var url = msg.Url!.Trim();
        // The same magnet can come with other trackers or as a .torrent: the info-hash tells.
        var existing = _manager.FindByUrl(url) ?? _manager.FindTorrent(TorrentLink.MagnetHash(url));
        if (existing is not null)
        {
            ShowFromTray();
            var answer = MessageBox.Show(this,
                $"«{existing.FileName}» ya está en la lista de descargas.\n\n" +
                "Sí: añadir de nuevo con otro nombre\nNo: " +
                (existing.Status == DownloadStatus.Completed ? "mostrar la descarga existente" : "reanudar la descarga existente"),
                "Enlace duplicado", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel)
                return Task.FromResult(false);
            if (answer == MessageBoxResult.No)
            {
                if (existing.Status != DownloadStatus.Completed)
                {
                    _manager.Start(existing);
                    ShowProgress(existing);
                }
                else
                {
                    ShowFromTray();
                    DownloadsGrid.SelectedItem = existing;
                    DownloadsGrid.ScrollIntoView(existing);
                }
                return Task.FromResult(true);
            }
        }

        var kind = KindOf(msg.Kind, url, msg.FileName);
        var item = _manager.CreateItem(url, msg.FileName, msg.Referrer, msg.Cookies, msg.UserAgent, kind);
        var dialog = new DownloadInfoWindow(_manager, item, msg.FileSize);
        if (IsVisible)
            dialog.Owner = this;
        var decision = new TaskCompletionSource<bool>();
        dialog.Accepted += startNow =>
        {
            _manager.Add(item, startNow);
            if (startNow && _manager.Settings.ShowProgressWindow)
                ShowProgress(item);
            decision.TrySetResult(true);
        };
        dialog.Closed += (_, _) => decision.TrySetResult(false);
        dialog.Show();
        dialog.Activate();
        return decision.Task;
    }

    /// <summary>The kind sent by the browser, or guessed from the address for a pasted link.</summary>
    private static DownloadKind KindOf(string? kind, string url, string? fileName = null)
    {
        // The browser reports a .torrent download as a plain file: OwnIDM fetches it and downloads its content.
        if (string.Equals(kind, "torrent", StringComparison.OrdinalIgnoreCase) || kind is null or "file" &&
            (TorrentLink.IsMagnet(url) || TorrentLink.IsTorrentFile(url) || TorrentLink.IsTorrentUrl(url, fileName)))
            return DownloadKind.Torrent;
        if (string.Equals(kind, "hls", StringComparison.OrdinalIgnoreCase))
            return DownloadKind.Hls;
        if (string.Equals(kind, "dash", StringComparison.OrdinalIgnoreCase))
            return DownloadKind.Dash;
        if (string.Equals(kind, "ytdlp", StringComparison.OrdinalIgnoreCase))
            return DownloadKind.YtDlp;
        if (kind is null && Uri.TryCreate(url, UriKind.Absolute, out var u))
        {
            if (u.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
                return DownloadKind.Hls;
            if (u.AbsolutePath.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase))
                return DownloadKind.Dash;
            if (IsYouTubeVideo(u))
                return DownloadKind.YtDlp;
        }
        return DownloadKind.Http;
    }

    private static bool IsYouTubeVideo(Uri u)
    {
        var host = u.Host.ToLowerInvariant();
        if (host == "youtu.be")
            return u.AbsolutePath.Length > 1;
        if (host is not ("youtube.com" or "www.youtube.com" or "m.youtube.com" or "music.youtube.com"))
            return false;
        return u.AbsolutePath == "/watch" && u.Query.Contains("v=", StringComparison.Ordinal) ||
               u.AbsolutePath.StartsWith("/shorts/", StringComparison.Ordinal) ||
               u.AbsolutePath.StartsWith("/live/", StringComparison.Ordinal);
    }

    private void OnAddUrl(object sender, RoutedEventArgs e)
    {
        var dialog = new AddUrlWindow { Owner = this };
        if (dialog.ShowDialog() == true)
            _ = OfferDownload(new IpcMessage { Url = dialog.Url });
    }

    private void OnOpenTorrent(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Archivos torrent (*.torrent)|*.torrent", Multiselect = true };
        if (dialog.ShowDialog(this) != true)
            return;
        foreach (var file in dialog.FileNames)
            _ = OfferDownload(new IpcMessage { Url = file });
    }

    private void OnBatch(object sender, RoutedEventArgs e)
    {
        var dialog = new BatchWindow { Owner = this };
        if (dialog.ShowDialog() != true)
            return;
        foreach (var url in dialog.Urls)
            _manager.Add(_manager.CreateItem(url), startNow: false);
        if (dialog.StartQueue)
            _manager.StartQueue();
        UpdateToolbarState();
    }

    private void OnImport(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Archivos de texto (*.txt)|*.txt|Todos los archivos (*.*)|*.*" };
        if (dialog.ShowDialog(this) != true)
            return;
        int added = 0;
        foreach (var line in File.ReadAllLines(dialog.FileName))
        {
            var url = line.Trim();
            if (Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme.StartsWith("http") && _manager.FindByUrl(url) is null)
            {
                _manager.Add(_manager.CreateItem(url), startNow: false);
                added++;
            }
        }
        MessageBox.Show(this, $"Se añadieron {added} descargas a la cola principal.", "Importar", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnExport(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "Archivos de texto (*.txt)|*.txt", FileName = "descargas.txt" };
        if (dialog.ShowDialog(this) == true)
            File.WriteAllLines(dialog.FileName, _manager.Items.Select(i => i.Url));
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.UnicodeText) || e.Data.GetDataPresent(DataFormats.Text) ||
                    e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            foreach (var file in files.Where(TorrentLink.IsTorrentFile))
                _ = OfferDownload(new IpcMessage { Url = file });
            return;
        }
        var text = (e.Data.GetData(DataFormats.UnicodeText) ?? e.Data.GetData(DataFormats.Text)) as string;
        var url = text?.Trim().Split('\n').FirstOrDefault()?.Trim();
        if (url is not null && (Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme.StartsWith("http") || TorrentLink.IsMagnet(url)))
            _ = OfferDownload(new IpcMessage { Url = url });
    }

    // ---------------------------------------------------------------- actions on selection

    private List<DownloadItem> Selected => DownloadsGrid.SelectedItems.OfType<DownloadItem>().ToList();

    private void OnResume(object sender, RoutedEventArgs e)
    {
        foreach (var item in Selected.Where(i => i.Status != DownloadStatus.Completed))
        {
            _manager.Start(item);
            if (_manager.Settings.ShowProgressWindow && Selected.Count == 1)
                ShowProgress(item);
        }
    }

    private void OnStop(object sender, RoutedEventArgs e)
    {
        foreach (var item in Selected)
            _manager.Pause(item);
    }

    private void OnStopAll(object sender, RoutedEventArgs e) => _manager.PauseAll();

    private async void OnDelete(object sender, RoutedEventArgs e)
    {
        var items = Selected;
        if (items.Count == 0)
            return;
        var dialog = new ConfirmDeleteWindow(items) { Owner = this };
        if (dialog.ShowDialog() != true)
            return;
        foreach (var item in items)
        {
            if (_progressWindows.TryGetValue(item.Id, out var w))
                w.Close();
            await _manager.RemoveAsync(item, dialog.DeleteFiles);
        }
    }

    private void OnDeleteCompleted(object sender, RoutedEventArgs e) => _manager.RemoveCompleted();

    private async void OnRedownload(object sender, RoutedEventArgs e)
    {
        foreach (var item in Selected)
            await _manager.RedownloadAsync(item);
    }

    private void OnShowProgress(object sender, RoutedEventArgs e)
    {
        foreach (var item in Selected)
            ShowProgress(item);
    }

    private void OnCopyUrl(object sender, RoutedEventArgs e)
    {
        if (Selected.FirstOrDefault() is { } item)
        {
            _ignoreNextClipboard = true;
            Clipboard.SetText(item.Url);
        }
    }

    private void OnOpenFile(object sender, RoutedEventArgs e)
    {
        if (Selected.FirstOrDefault() is { Status: DownloadStatus.Completed } item)
            Shell.Open(item.FullPath);
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        if (Selected.FirstOrDefault() is { } item)
            Shell.ShowInFolder(item);
    }

    private void OnGridDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (DownloadsGrid.SelectedItem is not DownloadItem item)
            return;
        if (item.Status == DownloadStatus.Completed)
            Shell.Open(item.FullPath);
        else
            ShowProgress(item);
    }

    private void OnToggleQueue(object sender, RoutedEventArgs e)
    {
        if (_manager.QueueRunning)
            _manager.StopQueue();
        else
            _manager.StartQueue();
        UpdateToolbarState();
    }

    private void OnScheduler(object sender, RoutedEventArgs e)
    {
        new SchedulerWindow(_manager) { Owner = this }.ShowDialog();
        UpdateToolbarState();
    }

    private void OnGrabber(object sender, RoutedEventArgs e) => new GrabberWindow(_manager) { Owner = this }.Show();

    private void OnAddToQueue(object sender, RoutedEventArgs e)
    {
        foreach (var item in Selected)
            _manager.SetInQueue(item, true);
    }

    private void OnRemoveFromQueue(object sender, RoutedEventArgs e)
    {
        foreach (var item in Selected)
            _manager.SetInQueue(item, false);
    }

    private void OnQueueCompleted()
    {
        var action = _manager.Settings.Schedule.OnComplete;
        _tray.ShowBalloonTip(3000, "OwnIDM", "La cola de descargas ha terminado.", WinForms.ToolTipIcon.Info);
        if (action == CompletionAction.None || _countdown is not null)
            return;
        _countdown = new CountdownWindow(_manager, action, ExitApp);
        _countdown.Closed += (_, _) => _countdown = null;
        _countdown.Show();
    }

    private void OnStartQueue(object sender, RoutedEventArgs e) { _manager.StartQueue(); UpdateToolbarState(); }

    private void OnStopQueue(object sender, RoutedEventArgs e) { _manager.StopQueue(); UpdateToolbarState(); }

    private void OnToggleLimiter(object sender, RoutedEventArgs e)
    {
        _manager.Settings.SpeedLimiterEnabled = !_manager.Settings.SpeedLimiterEnabled;
        _manager.SaveSettings();
        UpdateToolbarState();
    }

    private void OnOptions(object sender, RoutedEventArgs e)
    {
        var dialog = new OptionsWindow(_manager) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            BuildCategoryTree();
            UpdateToolbarState();
        }
    }

    private void OnAbout(object sender, RoutedEventArgs e) =>
        MessageBox.Show(this,
            $"OwnIDM {App.Version.ToString(3)}\n\nGestor de descargas multi-conexión con segmentación dinámica,\n" +
            "reanudación, colas, categorías, limitador de velocidad e integración con navegadores.",
            "Acerca de OwnIDM", MessageBoxButton.OK, MessageBoxImage.Information);

    // ---------------------------------------------------------------- updates

    private bool _checkingUpdates;
    private AppRelease? _pendingRelease;

    private async void OnCheckUpdates(object sender, RoutedEventArgs e) => await CheckForUpdatesAsync(manual: true);

    /// <summary>
    /// At startup (manual = false) checks once a day, quietly, and does not offer a skipped version;
    /// from the menu it always checks and says when there is nothing new.
    /// </summary>
    public async Task CheckForUpdatesAsync(bool manual)
    {
        var settings = _manager.Settings;
        if (_checkingUpdates)
            return;
        if (!App.Updater.Enabled)
        {
            if (manual)
                MessageBox.Show(this, "Esta copia de OwnIDM no tiene configurada una dirección de actualizaciones.",
                    "Buscar actualizaciones", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!manual && (!settings.CheckForUpdates || DateTime.UtcNow - settings.LastUpdateCheck < TimeSpan.FromDays(1)))
            return;

        _checkingUpdates = true;
        AppRelease? release;
        try
        {
            release = await App.Updater.CheckAsync(CancellationToken.None);
            settings.LastUpdateCheck = DateTime.UtcNow;
            _manager.SaveSettings();
        }
        catch (Exception ex) when (ex is IOException or System.Net.Http.HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            if (manual)
                MessageBox.Show(this, "No se pudo comprobar si hay una versión nueva:\n" + ex.Message,
                    "Buscar actualizaciones", MessageBoxButton.OK, MessageBoxImage.Warning);
            return; // offline at startup: try again next time
        }
        finally
        {
            _checkingUpdates = false;
        }

        if (release is null)
        {
            if (manual)
                MessageBox.Show(this, $"Tienes la última versión de OwnIDM ({App.Updater.CurrentVersion}).",
                    "Buscar actualizaciones", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!manual && release.Version.ToString() == settings.SkippedVersion)
            return;
        if (!IsVisible)
        {
            // Started in the tray: no window pops up, the tray icon tells instead.
            _pendingRelease = release;
            _tray.BalloonTipClicked -= OnUpdateBalloonClicked;
            _tray.BalloonTipClicked += OnUpdateBalloonClicked;
            _tray.ShowBalloonTip(8000, "Actualización disponible",
                $"OwnIDM {release.Version} está disponible. Haz clic para actualizar.", WinForms.ToolTipIcon.Info);
            return;
        }
        OfferUpdate(release);
    }

    private void OnUpdateBalloonClicked(object? sender, EventArgs e)
    {
        _tray.BalloonTipClicked -= OnUpdateBalloonClicked;
        if (_pendingRelease is not { } release)
            return;
        _pendingRelease = null;
        ShowFromTray();
        OfferUpdate(release);
    }

    private void OfferUpdate(AppRelease release)
    {
        var dialog = new UpdateWindow(_manager, release) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.InstallerPath is not { } installer)
            return;
        try
        {
            // /RELAUNCH=1 makes the installer open OwnIDM again when it finishes (see OwnIDM.iss).
            Process.Start(new ProcessStartInfo(installer, "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /RELAUNCH=1")
            {
                UseShellExecute = true,
            });
        }
        catch (Win32Exception ex)
        {
            MessageBox.Show(this, "No se pudo iniciar el instalador:\n" + ex.Message, "Actualización de OwnIDM",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        ExitApp(); // frees OwnIDM's files for the installer
    }

    // ---------------------------------------------------------------- progress / completion

    public void ShowProgress(DownloadItem item)
    {
        if (_progressWindows.TryGetValue(item.Id, out var existing))
        {
            existing.Activate();
            return;
        }
        var window = new ProgressWindow(_manager, item);
        _progressWindows[item.Id] = window;
        window.Closed += (_, _) => _progressWindows.Remove(item.Id);
        window.Show();
    }

    private void OnDownloadFinished(DownloadItem item)
    {
        if (item.Status != DownloadStatus.Completed)
            return;
        if (_progressWindows.TryGetValue(item.Id, out var w))
            w.Close();
        if (_manager.Settings.ShowCompleteDialog && !item.InQueue)
            new CompleteWindow(_manager, item).Show();
        else
            _tray.ShowBalloonTip(3000, "Descarga completada", item.FileName, WinForms.ToolTipIcon.Info);
    }

    // ---------------------------------------------------------------- tray & lifetime

    private WinForms.NotifyIcon CreateTrayIcon()
    {
        var iconStream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/ownidm.ico"))!.Stream;
        var tray = new WinForms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(iconStream),
            Text = "OwnIDM",
            Visible = true,
            ContextMenuStrip = new WinForms.ContextMenuStrip(),
        };
        tray.ContextMenuStrip.Items.Add("Abrir OwnIDM", null, (_, _) => ShowFromTray());
        tray.ContextMenuStrip.Items.Add("Añadir descarga…", null, (_, _) => { ShowFromTray(); OnAddUrl(this, new RoutedEventArgs()); });
        tray.ContextMenuStrip.Items.Add(new WinForms.ToolStripSeparator());
        tray.ContextMenuStrip.Items.Add("Salir", null, (_, _) => ExitApp());
        tray.MouseClick += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Left)
                ShowFromTray();
        };
        return tray;
    }

    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_exiting)
        {
            // Closing the window keeps OwnIDM in the tray, like a classic download manager.
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnClosing(e);
    }

    private void OnExit(object sender, RoutedEventArgs e) => ExitApp();

    private async void ExitApp()
    {
        if (_exiting)
            return;
        _exiting = true;
        _timer.Stop();
        await _manager.ShutdownAsync();
        _tray.Visible = false;
        _tray.Dispose();
        Application.Current.Shutdown();
    }

    // ---------------------------------------------------------------- clipboard monitor

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var source = (HwndSource)PresentationSource.FromVisual(this)!;
        source.AddHook(WndProc);
        AddClipboardFormatListener(source.Handle);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_CLIPBOARDUPDATE)
            Dispatcher.BeginInvoke(OnClipboardChanged);
        return IntPtr.Zero;
    }

    private void OnClipboardChanged()
    {
        if (_ignoreNextClipboard)
        {
            _ignoreNextClipboard = false;
            return;
        }
        if (!_manager.Settings.MonitorClipboard)
            return;
        string text;
        try
        {
            if (!Clipboard.ContainsText())
                return;
            text = Clipboard.GetText().Trim();
        }
        catch (COMException)
        {
            return; // clipboard locked by another app
        }
        if (text == _lastClipboardUrl || !_manager.Settings.IsMonitoredUrl(text))
            return;
        _lastClipboardUrl = text;
        _ = OfferDownload(new IpcMessage { Url = text });
    }
}

public static class Shell
{
    /// <summary>Opens a file, or a folder (a torrent with several files) in Explorer.</summary>
    public static void Open(string path)
    {
        if (File.Exists(path) || Directory.Exists(path))
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        else
            MessageBox.Show($"No se encontró el archivo:\n{path}", "OwnIDM", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    public static void ShowInFolder(DownloadItem item)
    {
        if (File.Exists(item.FullPath) || Directory.Exists(item.FullPath))
            Process.Start("explorer.exe", $"/select,\"{item.FullPath}\"");
        else if (Directory.Exists(item.SaveDirectory))
            Process.Start("explorer.exe", $"\"{item.SaveDirectory}\"");
    }
}
