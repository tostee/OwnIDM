using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using Microsoft.Win32;
using OwnIDM.Core.Engine;
using OwnIDM.Core.Grabber;
using OwnIDM.Core.Manager;

namespace OwnIDM.App;

public partial class GrabberWindow : Window
{
    private static readonly (string Label, string Masks)[] Presets =
    [
        ("Comprimidos", "zip rar 7z gz bz2 xz tar tgz iso"),
        ("Documentos", "pdf doc docx xls xlsx ppt pptx odt ods txt epub csv"),
        ("Imágenes", "jpg jpeg png gif webp bmp svg tif tiff"),
        ("Música", "mp3 m4a flac wav aac ogg opus wma"),
        ("Vídeo", "mp4 mkv avi mov wmv webm flv m4v mpg mpeg"),
        ("Programas", "exe msi msix apk dmg deb rpm"),
        ("Cualquier archivo (no páginas)", "*"),
        ("Personalizado", ""),
    ];

    private readonly DownloadManager _manager;
    private readonly ObservableCollection<GrabbedRow> _rows = new();
    private CancellationTokenSource? _cts;

    public GrabberWindow(DownloadManager manager, string? startUrl = null)
    {
        InitializeComponent();
        _manager = manager;
        ResultsGrid.ItemsSource = _rows;
        _rows.CollectionChanged += (_, _) => AddButton.IsEnabled = _rows.Count > 0;

        var o = manager.Settings.Grabber;
        StartUrlBox.Text = startUrl ?? o.StartUrl;
        DepthBox.Text = o.Depth.ToString(CultureInfo.InvariantCulture);
        ExternalDepthBox.Text = o.ExternalDepth.ToString(CultureInfo.InvariantCulture);
        MaxPagesBox.Text = o.MaxPages.ToString(CultureInfo.InvariantCulture);
        ParallelBox.Text = o.Parallel.ToString(CultureInfo.InvariantCulture);
        StayBelowBox.IsChecked = o.StayBelowStartPath;
        RobotsBox.IsChecked = o.RespectRobots;
        PresetBox.ItemsSource = Presets.Select(p => p.Label).ToList();
        int preset = Array.FindIndex(Presets, p => p.Masks == o.FileMasks);
        PresetBox.SelectedIndex = preset >= 0 ? preset : Presets.Length - 1;
        MasksBox.Text = o.FileMasks;
        SameSiteFilesBox.IsChecked = o.FilesFromStartSiteOnly;
        HideDuplicatesBox.IsChecked = o.HideDuplicates;
        ByCategoryRadio.IsChecked = true;
        FolderBox.Text = Path.Combine(manager.Settings.DefaultDownloadDirectory, "Grabber");
        UpdateStats(new GrabberProgress(0, 0, 0, null));

        Closed += (_, _) => _cts?.Cancel();
    }

    private void OnPresetChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (PresetBox.SelectedIndex >= 0 && Presets[PresetBox.SelectedIndex].Masks is { Length: > 0 } masks)
            MasksBox.Text = masks;
    }

    private static int ReadInt(string text, int min, int max, int fallback) =>
        int.TryParse(text.Trim(), out var v) ? Math.Clamp(v, min, max) : fallback;

    private GrabberOptions ReadOptions() => new()
    {
        StartUrl = StartUrlBox.Text.Trim(),
        Depth = ReadInt(DepthBox.Text, 0, 20, 1),
        ExternalDepth = ReadInt(ExternalDepthBox.Text, 0, 5, 0),
        MaxPages = ReadInt(MaxPagesBox.Text, 1, 100_000, 300),
        Parallel = ReadInt(ParallelBox.Text, 1, 10, 4),
        StayBelowStartPath = StayBelowBox.IsChecked == true,
        RespectRobots = RobotsBox.IsChecked == true,
        FileMasks = MasksBox.Text.Trim(),
        FilesFromStartSiteOnly = SameSiteFilesBox.IsChecked == true,
        HideDuplicates = HideDuplicatesBox.IsChecked == true,
    };

    private async void OnExplore(object sender, RoutedEventArgs e)
    {
        var options = ReadOptions();
        if (!Uri.TryCreate(options.StartUrl, UriKind.Absolute, out var u) || !u.Scheme.StartsWith("http"))
        {
            MessageBox.Show(this, "Introduce una dirección inicial http:// o https:// válida.", "Grabber", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (string.IsNullOrWhiteSpace(options.FileMasks))
        {
            MessageBox.Show(this, "Indica qué archivos buscar (por ejemplo: zip pdf).", "Grabber", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _manager.Settings.Grabber = options;
        _manager.SaveSettings();

        _rows.Clear();
        _cts = new CancellationTokenSource();
        SetRunning(true);
        var grabber = new SiteGrabber();
        grabber.FileFound += f => Dispatcher.BeginInvoke(() => _rows.Add(new GrabbedRow(f)));
        var progress = new Progress<GrabberProgress>(UpdateStats);
        try
        {
            var result = await grabber.RunAsync(options, progress, _cts.Token);
            CurrentText.Text = result.HitPageLimit
                ? $"Terminado: se alcanzó el límite de {options.MaxPages} páginas."
                : "Exploración terminada.";
        }
        catch (OperationCanceledException)
        {
            CurrentText.Text = "Exploración detenida.";
        }
        catch (Exception ex)
        {
            CurrentText.Text = "Error: " + ex.Message;
        }
        finally
        {
            SetRunning(false);
            _cts.Dispose();
            _cts = null;
        }
    }

    private void UpdateStats(GrabberProgress p)
    {
        StatsText.Text = $"Páginas exploradas: {p.PagesExplored}   ·   Archivos encontrados: {Math.Max(p.FilesFound, _rows.Count)}";
        if (p.CurrentUrl is not null)
            CurrentText.Text = p.CurrentUrl;
    }

    private void SetRunning(bool running)
    {
        ExploreButton.IsEnabled = !running;
        StopButton.IsEnabled = running;
    }

    private void OnStop(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void OnCheckAll(object sender, RoutedEventArgs e)
    {
        foreach (var r in _rows)
            r.Checked = true;
    }

    private void OnUncheckAll(object sender, RoutedEventArgs e)
    {
        foreach (var r in _rows)
            r.Checked = false;
    }

    private void OnBrowseFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { InitialDirectory = Directory.Exists(FolderBox.Text) ? FolderBox.Text : null };
        if (dialog.ShowDialog(this) == true)
        {
            FolderBox.Text = dialog.FolderName;
            ToFolderRadio.IsChecked = true;
        }
    }

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        var selected = _rows.Where(r => r.Checked).ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show(this, "Marca los archivos que quieres descargar.", "Grabber", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        int added = 0, skipped = 0;
        foreach (var row in selected)
        {
            if (_manager.FindByUrl(row.Url) is not null)
            {
                skipped++;
                continue;
            }
            var item = _manager.CreateItem(row.Url, row.FileName, referrer: row.PageUrl);
            var baseDir = ToFolderRadio.IsChecked == true ? FolderBox.Text.Trim() : item.SaveDirectory;
            item.SaveDirectory = SubfoldersBox.IsChecked == true ? Path.Combine(baseDir, SiteSubfolder(row.Url)) : baseDir;
            item.Description = "Grabber: " + row.PageUrl;
            _manager.Add(item, startNow: false);
            row.Checked = false;
            added++;
        }
        if (StartQueueBox.IsChecked == true && added > 0)
            _manager.StartQueue();
        CurrentText.Text = $"Añadidos {added} archivos a la cola principal" + (skipped > 0 ? $" ({skipped} ya estaban en la lista)." : ".");
    }

    /// <summary>"host\dir\sub" for the URL's folder, keeping the site's structure on disk.</summary>
    private static string SiteSubfolder(string url)
    {
        var uri = new Uri(url);
        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).SkipLast(1)
                       .Select(p => FileNameHelper.Sanitize(Uri.UnescapeDataString(p)));
        return Path.Combine(new[] { FileNameHelper.Sanitize(uri.Host) }.Concat(parts).ToArray());
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    public sealed class GrabbedRow(GrabbedFile file) : INotifyPropertyChanged
    {
        private bool _checked = true;

        public string Url { get; } = file.Url;
        public string FileName { get; } = file.FileName;
        public long Size { get; } = file.Size;
        public string? ContentType { get; } = file.ContentType;
        public string PageUrl { get; } = file.PageUrl;

        public bool Checked
        {
            get => _checked;
            set
            {
                if (_checked == value)
                    return;
                _checked = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string name = "") =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
