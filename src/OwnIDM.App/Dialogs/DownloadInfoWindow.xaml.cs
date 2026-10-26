using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using OwnIDM.Core.Engine;
using OwnIDM.Core.Hls;
using OwnIDM.Core.Manager;
using OwnIDM.Core.Models;
using OwnIDM.Core.Tools;
using OwnIDM.Core.Torrents;
using OwnIDM.Core.Util;
using OwnIDM.Core.YtDlp;

namespace OwnIDM.App;

/// <summary>Shown before a download starts: category, destination, description, size.</summary>
public partial class DownloadInfoWindow : Window
{
    private readonly DownloadManager _manager;
    private readonly DownloadItem _item;
    private readonly CancellationTokenSource _probe = new();
    private List<Tool> _missingTools = [];
    private bool _settingPath;
    private bool _pathEditedByUser;
    private bool _accepted;
    // yt-dlp: the qualities found and what the probe said, shown after the size of the chosen one.
    private List<YtDlpQuality> _ytDlpQualities = [];
    private string? _ytDlpInfo;
    // Torrent: what it holds, and the file list with the user's choice.
    private TorrentProbe? _torrent;
    private List<TorrentFileRow> _fileRows = [];

    private bool IsTorrent => _item.Kind == DownloadKind.Torrent;

    public DownloadInfoWindow(DownloadManager manager, DownloadItem item, long? sizeHint)
    {
        InitializeComponent();
        _manager = manager;
        _item = item;

        UrlBox.Text = item.Url;
        CategoryBox.ItemsSource = manager.Settings.Categories.Select(c => c.Name).ToList();
        _settingPath = true;
        CategoryBox.SelectedItem = item.Category;
        _settingPath = false;
        if (IsTorrent)
        {
            // A torrent is a file or a folder named by the torrent itself: only the destination folder is chosen.
            PathLabel.Text = "Guardar en";
            SetPath(item.SaveDirectory);
        }
        else
        {
            SetPath(item.FullPath);
        }
        if (sizeHint > 0)
            InfoText.Text = "Tamaño: " + Format.Bytes(sizeHint.Value);

        Loaded += async (_, _) => await ProbeAsync();
        QualityBox.SelectionChanged += (_, _) => ShowYtDlpInfo();
        Closed += (_, _) =>
        {
            _probe.Cancel();
            // The page saved by the probe is only of use to a download that was added.
            if (!_accepted && _item.Kind == DownloadKind.YtDlp)
                YtDlpDownloader.TryDeleteDirectory(_manager.YtDlp.TempDirectoryFor(_item));
            // Likewise the torrent metadata, unless another download in the list uses it.
            if (!_accepted && _torrent is not null && !_manager.Items.Any(i => i.InfoHash == _torrent.InfoHash))
                TryDelete(_manager.Torrents.MetadataPathFor(_torrent.InfoHash));
        };
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private YtDlpQuality? SelectedYtDlpQuality =>
        QualityBox.SelectedItem as YtDlpQuality ?? (_ytDlpQualities is [var only] ? only : null);

    private void ShowYtDlpInfo()
    {
        if (_ytDlpInfo is null)
            return;
        var size = SelectedYtDlpQuality is { Size: > 0 } q
            ? "Tamaño: " + (q.SizeExact ? Format.Bytes(q.Size.Value) : "≈ " + Format.Bytes(q.Size.Value) + " (estimado)")
            : "Tamaño: desconocido";
        InfoText.Text = $"{size}   ·   Reanudable: sí   ·   {_ytDlpInfo}";
    }

    /// <summary>Raised with true for "Iniciar descarga" and false for "Descargar más tarde".</summary>
    public event Action<bool>? Accepted;

    private async Task ProbeAsync()
    {
        if (_item.IsStream)
        {
            await ProbeStreamAsync();
            return;
        }
        if (_item.Kind == DownloadKind.YtDlp)
        {
            await ProbeYtDlpAsync();
            return;
        }
        if (IsTorrent)
        {
            await ProbeTorrentAsync();
            return;
        }
        try
        {
            var probe = await _manager.Engine.ProbeAsync(_item.Url, _item.Referrer, _item.Cookies, _item.UserAgent, _probe.Token);
            if (!_pathEditedByUser && probe.FileName != Path.GetFileName(PathBox.Text))
            {
                var category = _manager.Settings.CategoryFor(probe.FileName);
                _settingPath = true;
                CategoryBox.SelectedItem = category;
                _settingPath = false;
                SetPath(Path.Combine(_manager.Settings.DirectoryFor(category), probe.FileName));
            }
            InfoText.Text = $"Tamaño: {Format.Bytes(probe.TotalSize)}   ·   " +
                            (probe.ResumeSupported ? "Reanudable: sí (multi-conexión)" : "Reanudable: no (una sola conexión)");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            InfoText.Text = "No se pudo consultar el servidor: " + ex.Message;
        }
    }

    private async Task ProbeStreamAsync()
    {
        OfferTools([]);
        try
        {
            var probe = await _manager.Hls.ProbeAsync(_item.Url, _item.Referrer, _item.Cookies, _item.UserAgent, _probe.Token);
            var duration = TimeSpan.FromSeconds(probe.TotalSeconds);
            bool hasFfmpeg = Ffmpeg.Find(_manager.Settings) is not null;
            // DASH segments are fragmented MP4: they need ffmpeg only to join a separate audio track.
            bool dash = _item.Kind == DownloadKind.Dash;
            InfoText.Text = $"Vídeo {(dash ? "DASH" : "HLS")} · duración {(int)duration.TotalMinutes}:{duration.Seconds:00}" +
                            (probe.SegmentCount > 0 ? $" · {probe.SegmentCount} segmentos" : "") +
                            (probe.SeparateAudio ? " · audio separado, se unirá con ffmpeg"
                                : hasFfmpeg || dash ? " · se guardará como .mp4" : " · se guardará como .ts (instala ffmpeg para obtener .mp4)");
            if (probe.Variants.Count > 1)
            {
                QualityBox.ItemsSource = probe.Variants;
                QualityBox.SelectedIndex = 0;
                QualityPanel.Visibility = Visibility.Visible;
            }
            string? problem = probe.Encrypted
                ? "Este vídeo está cifrado o protegido (DRM) y no se puede descargar."
                : probe.IsLive ? "Es una emisión en directo; solo se pueden descargar vídeos completos."
                : probe.Unsupported ? (dash ? "Este vídeo DASH tiene varios periodos (p. ej. anuncios intercalados) y no está soportado."
                    : "Esta lista cambia de formato a mitad del vídeo y no está soportada.")
                : probe.SeparateAudio && !hasFfmpeg ? "Este vídeo trae el audio por separado y hace falta ffmpeg para unirlo."
                : null;
            if (problem is not null)
                ShowProblem(problem);
            if (probe.SeparateAudio && !hasFfmpeg && !probe.Encrypted && !probe.IsLive)
                OfferTools([Tool.Ffmpeg]);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            InfoText.Text = "No se pudo leer la lista de reproducción: " + ex.Message;
        }
    }

    private async Task ProbeYtDlpAsync()
    {
        var missing = ComponentsWindow.MissingForYtDlp(_manager.Settings);
        OfferTools(missing);
        if (missing.Contains(Tool.YtDlp))
        {
            ShowProblem("Para descargar vídeos de esta página OwnIDM necesita yt-dlp, un componente gratuito que se instala en un momento.");
            return;
        }
        InfoText.Text = "Consultando el vídeo con yt-dlp…";
        try
        {
            var probe = await _manager.YtDlp.ProbeAsync(_item.Url, _item.Cookies, _item.UserAgent, _probe.Token,
                saveInfoTo: _manager.YtDlp.InfoPathFor(_item));
            if (!_pathEditedByUser)
            {
                var name = FileNameHelper.Sanitize(probe.Title) + ".mp4";
                var category = _manager.Settings.CategoryFor(name);
                _settingPath = true;
                CategoryBox.SelectedItem = category;
                _settingPath = false;
                SetPath(Path.Combine(_manager.Settings.DirectoryFor(category), name));
            }
            var duration = TimeSpan.FromSeconds(probe.Duration);
            bool hasFfmpeg = Ffmpeg.Find(_manager.Settings) is not null;
            _ytDlpInfo = $"{(probe.Site.Length > 0 ? probe.Site : "Vídeo")} · duración {(int)duration.TotalMinutes}:{duration.Seconds:00} · se descargará con yt-dlp" +
                         (hasFfmpeg ? "" : " · sin ffmpeg solo hay calidades con el audio incluido (en YouTube, hasta 360p)") +
                         (missing.Contains(Tool.Deno) ? " · sin Deno, YouTube puede ofrecer pocas calidades o fallar" : "");
            _ytDlpQualities = probe.Qualities;
            if (probe.Qualities.Count > 1)
            {
                QualityBox.ItemsSource = probe.Qualities;
                QualityBox.SelectedIndex = 0;
                QualityPanel.Visibility = Visibility.Visible;
            }
            ShowYtDlpInfo();
            if (probe.IsLive)
                ShowProblem("Es una emisión en directo; solo se pueden descargar vídeos completos.");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            InfoText.Text = "yt-dlp no pudo leer el vídeo: " + ex.Message;
            InfoText.Foreground = System.Windows.Media.Brushes.Firebrick;
        }
    }

    /// <summary>
    /// Reads the torrent and lists its files. A magnet link first needs peers that send the file list,
    /// which can take a while: the download may start meanwhile (with every file).
    /// </summary>
    private async Task ProbeTorrentAsync()
    {
        bool magnet = TorrentLink.IsMagnet(_item.Url);
        InfoText.Text = magnet
            ? "Buscando pares que envíen la lista de archivos del torrent… Puedes iniciar ya la descarga: se bajarán todos los archivos."
            : "Leyendo el torrent…";
        try
        {
            var probe = await _manager.Torrents.ProbeAsync(_item.Url, _item.Referrer, _item.Cookies, _item.UserAgent, _probe.Token);
            if (_manager.FindTorrent(probe.InfoHash) is { } existing)
            {
                ShowProblem($"Este torrent ya está en la lista de descargas («{existing.FileName}»).");
                return;
            }
            _torrent = probe;
            _fileRows = probe.Files.Select(f => new TorrentFileRow(f.Clone())).ToList();
            FilesList.ItemsSource = _fileRows;
            TorrentPanel.Visibility = _fileRows.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            if (!_pathEditedByUser)
                SelectCategoryForFiles();
            ShowTorrentInfo();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (magnet)
            {
                // Peers may turn up later: the download keeps looking for them.
                InfoText.Text = ex.Message + " Puedes añadirlo igualmente; OwnIDM seguirá buscando pares.";
                InfoText.Foreground = System.Windows.Media.Brushes.DarkGoldenrod;
            }
            else
            {
                ShowProblem("No se pudo leer el torrent: " + ex.Message);
            }
        }
    }

    private void SelectCategoryForFiles()
    {
        if (TorrentLink.MainFile(_fileRows.Select(r => r.Entry)) is not { } main)
            return;
        var category = _manager.Settings.CategoryFor(main);
        _settingPath = true;
        CategoryBox.SelectedItem = category;
        _settingPath = false;
        SetPath(_manager.Settings.DirectoryFor(category));
    }

    private void ShowTorrentInfo()
    {
        if (_torrent is null)
            return;
        var selected = _fileRows.Where(r => r.Selected).ToList();
        var files = _fileRows.Count == 1 ? "1 archivo"
            : selected.Count == _fileRows.Count ? $"{_fileRows.Count} archivos"
            : $"{selected.Count} de {_fileRows.Count} archivos";
        InfoText.Text = $"Torrent «{_torrent.Name}»   ·   {files}, {Format.Bytes(selected.Sum(r => r.Entry.Length))}   ·   Reanudable: sí" +
                        (_torrent.IsPrivate ? "   ·   privado (solo pares del rastreador)" : "");
        InfoText.Foreground = (System.Windows.Media.Brush)FindResource("MutedTextBrush");
        StartButton.IsEnabled = LaterButton.IsEnabled = selected.Count > 0;
    }

    private void OnFileToggled(object sender, RoutedEventArgs e)
    {
        if (!_pathEditedByUser)
            SelectCategoryForFiles();
        ShowTorrentInfo();
    }

    private void OnSelectAllFiles(object sender, RoutedEventArgs e) => SelectAllFiles(true);

    private void OnSelectNoFiles(object sender, RoutedEventArgs e) => SelectAllFiles(false);

    private void SelectAllFiles(bool selected)
    {
        foreach (var row in _fileRows)
            row.Selected = selected;
        FilesList.Items.Refresh();
        ShowTorrentInfo();
    }

    private void ShowProblem(string problem)
    {
        _ytDlpInfo = null; // a later change of quality must not hide it
        InfoText.Text = problem;
        InfoText.Foreground = System.Windows.Media.Brushes.Firebrick;
        StartButton.IsEnabled = LaterButton.IsEnabled = false;
    }

    /// <summary>Shows "Instalar componentes…" for the tools this download is missing (none hides it).</summary>
    private void OfferTools(List<Tool> missing)
    {
        _missingTools = missing;
        InstallToolsButton.Visibility = missing.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        InstallToolsButton.Content = "Instalar " + string.Join(", ", missing.Select(ToolManager.Display)) + "…";
    }

    private async void OnInstallTools(object sender, RoutedEventArgs e)
    {
        var reason = _item.Kind == DownloadKind.YtDlp
            ? "Para descargar vídeos de YouTube y sitios similares OwnIDM usa estos componentes gratuitos y de código abierto:"
            : "Para unir el vídeo y el audio de esta descarga OwnIDM usa este componente gratuito y de código abierto:";
        var dialog = new ComponentsWindow(_manager, _missingTools, reason) { Owner = this };
        if (dialog.ShowDialog() != true)
            return;
        // Start over with the tools in place.
        InfoText.Foreground = (System.Windows.Media.Brush)FindResource("MutedTextBrush");
        StartButton.IsEnabled = LaterButton.IsEnabled = true;
        await ProbeAsync();
    }

    private void SetPath(string path)
    {
        _settingPath = true;
        PathBox.Text = path;
        _settingPath = false;
    }

    private void OnPathEdited(object sender, TextChangedEventArgs e)
    {
        if (!_settingPath)
            _pathEditedByUser = true;
    }

    private void OnCategoryChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_settingPath || CategoryBox.SelectedItem is not string category)
            return;
        SetPath(IsTorrent
            ? _manager.Settings.DirectoryFor(category)
            : Path.Combine(_manager.Settings.DirectoryFor(category), Path.GetFileName(PathBox.Text)));
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        if (IsTorrent)
        {
            var folder = new OpenFolderDialog
            {
                InitialDirectory = Directory.Exists(PathBox.Text) ? PathBox.Text : _manager.Settings.DefaultDownloadDirectory,
            };
            if (folder.ShowDialog(this) == true)
            {
                SetPath(folder.FolderName);
                _pathEditedByUser = true;
            }
            return;
        }
        var dir = Path.GetDirectoryName(PathBox.Text);
        var dialog = new SaveFileDialog
        {
            FileName = Path.GetFileName(PathBox.Text),
            InitialDirectory = Directory.Exists(dir) ? dir : _manager.Settings.DefaultDownloadDirectory,
            Filter = "Todos los archivos (*.*)|*.*",
            OverwritePrompt = false,
        };
        if (dialog.ShowDialog(this) == true)
        {
            SetPath(dialog.FileName);
            _pathEditedByUser = true;
        }
    }

    private void OnStartNow(object sender, RoutedEventArgs e) => Accept(startNow: true);

    private void OnLater(object sender, RoutedEventArgs e) => Accept(startNow: false);

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private void Accept(bool startNow)
    {
        if (IsTorrent)
        {
            AcceptTorrent(startNow);
            return;
        }
        var path = PathBox.Text.Trim();
        var name = Path.GetFileName(path);
        var dir = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(dir) || !Path.IsPathRooted(path))
        {
            MessageBox.Show(this, "Indica una ruta completa, por ejemplo C:\\Descargas\\archivo.zip", "Guardar como",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _item.FileName = FileNameHelper.Sanitize(name);
        _item.SaveDirectory = dir;
        _item.Category = CategoryBox.SelectedItem as string ?? _item.Category;
        _item.Description = string.IsNullOrWhiteSpace(DescriptionBox.Text) ? null : DescriptionBox.Text.Trim();
        if (QualityBox.SelectedItem is HlsVariant variant)
        {
            // Keep the master playlist / manifest: it also names the separate audio track, if any.
            _item.SourceUrl ??= _item.Url;
            _item.Url = variant.Url.ToString();
            _item.Description ??= "Vídeo " + variant.Label;
        }
        else if (SelectedYtDlpQuality is { } quality)
        {
            _item.YtDlpQuality = quality.Key;
            _item.YtDlpFormat = quality.Formats;
            _item.ResumeSupported = true;
            if (quality.Size > 0)
                _item.TotalSize = quality.Size.Value;
            if (quality.Key != YtDlpTool.BestKey)
                _item.Description ??= quality.Key == YtDlpTool.AudioKey ? "Audio" : "Vídeo " + quality.Label;
        }
        _accepted = true;
        Accepted?.Invoke(startNow);
        Close();
    }

    private void AcceptTorrent(bool startNow)
    {
        var dir = PathBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(dir) || !Path.IsPathRooted(dir))
        {
            MessageBox.Show(this, "Indica una carpeta completa, por ejemplo C:\\Descargas", "Guardar en",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        // Another dialog may have added the same torrent meanwhile.
        if (_manager.FindTorrent(_torrent?.InfoHash ?? TorrentLink.MagnetHash(_item.Url)) is { } existing)
        {
            MessageBox.Show(this, $"Este torrent ya está en la lista de descargas («{existing.FileName}»).", "Torrent",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _item.SaveDirectory = dir;
        _item.Category = CategoryBox.SelectedItem as string ?? _item.Category;
        _item.Description = string.IsNullOrWhiteSpace(DescriptionBox.Text) ? null : DescriptionBox.Text.Trim();
        if (_torrent is not null)
        {
            _item.InfoHash = _torrent.InfoHash;
            // Provisional: the downloader sets the name of the file or folder the torrent really creates.
            _item.FileName = FileNameHelper.Sanitize(_torrent.Name);
            lock (_item.SyncRoot)
            {
                _item.TorrentFiles.Clear();
                _item.TorrentFiles.AddRange(_fileRows.Select(r => r.Entry));
            }
            _item.TotalSize = _fileRows.Where(r => r.Selected).Sum(r => r.Entry.Length);
        }
        _item.ResumeSupported = true;
        _accepted = true;
        Accepted?.Invoke(startNow);
        Close();
    }
}

/// <summary>A file of the torrent in the list: its check box and size.</summary>
public sealed class TorrentFileRow(TorrentFileEntry entry)
{
    public TorrentFileEntry Entry { get; } = entry;
    public bool Selected { get => Entry.Selected; set => Entry.Selected = value; }
    public string Path => Entry.Path;
    public string Size => Format.Bytes(Entry.Length);
}
