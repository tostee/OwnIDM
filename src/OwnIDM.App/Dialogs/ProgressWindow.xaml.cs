using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using OwnIDM.Core.Manager;
using OwnIDM.Core.Models;
using OwnIDM.Core.Util;

namespace OwnIDM.App;

public partial class ProgressWindow : Window
{
    private readonly DownloadManager _manager;
    private readonly DownloadItem _item;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };

    public ProgressWindow(DownloadManager manager, DownloadItem item)
    {
        InitializeComponent();
        _manager = manager;
        _item = item;
        Segments.Item = item;
        if (item.Kind == DownloadKind.Torrent)
        {
            DetailsExpander.Header = "Archivos del torrent";
            StartColumn.Header = "Archivo";
            StartColumn.Width = 220;
        }
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Closed += (_, _) => _timer.Stop();
        Refresh();
    }

    private void Refresh()
    {
        var item = _item;
        Title = item.TotalSize > 0 && item.Status == DownloadStatus.Downloading
            ? $"{item.Progress:0}% {item.FileName}"
            : item.FileName;
        FileNameText.Text = item.FileName;
        UrlText.Text = item.Url;
        StatusValue.Text = item.Status == DownloadStatus.Error
            ? "Error: " + item.ErrorMessage
            : StatusTextConverter.Describe(item.Status, item.Progress, item.TotalSize) +
              (item.Kind == DownloadKind.Torrent && item.IsActive ? $" · {item.ActiveConnections} pares conectados" : "");
        StatusValue.Foreground = item.Status == DownloadStatus.Error ? Brushes.Firebrick : Brushes.Black;
        SizeValue.Text = Format.Bytes(item.TotalSize);
        DownloadedValue.Text = item.TotalSize > 0
            ? $"{Format.Bytes(item.Downloaded)} ({item.Progress:0.00}%)"
            : Format.Bytes(item.Downloaded);
        SpeedValue.Text = Format.Speed(item.Speed);
        TimeLeftValue.Text = Format.Duration(item.TimeLeft);
        ResumeValue.Text = item.ResumeSupported switch { true => "Sí", false => "No", _ => "(desconocido)" };
        Bar.IsIndeterminate = item.IsActive && item.TotalSize <= 0;
        Bar.Value = item.Progress;

        bool running = _manager.IsRunning(item);
        PauseButton.Content = running ? "Pausar" : "Reanudar";
        PauseButton.IsEnabled = item.Status != DownloadStatus.Completed;

        int n = 0;
        if (item.Kind == DownloadKind.Torrent)
        {
            ConnectionsList.ItemsSource = item.SnapshotTorrentFiles().Select(f => new
            {
                Number = ++n,
                Start = f.Path,
                Downloaded = Format.Bytes(f.Downloaded),
                Remaining = f.Selected ? Format.Bytes(Math.Max(0, f.Length - f.Downloaded)) : "-",
                Info = !f.Selected ? "No se descarga" : f.Downloaded >= f.Length ? "Completado" : running ? "Descargando…" : "En espera",
            }).ToList();
            Segments.InvalidateVisual();
            return;
        }
        ConnectionsList.ItemsSource = item.SnapshotSegments().Select(s => new
        {
            Number = ++n,
            Start = Format.Bytes(s.Start),
            Downloaded = Format.Bytes(s.Downloaded),
            Remaining = s.End < 0 ? "?" : Format.Bytes(Math.Max(0, s.Remaining)),
            Info = s.IsDone ? "Completado" : s.Active ? (s.Downloaded > 0 ? "Recibiendo datos…" : "Conectando…") : "En espera",
        }).ToList();
        Segments.InvalidateVisual();
    }

    private void OnPauseResume(object sender, RoutedEventArgs e)
    {
        if (_manager.IsRunning(_item))
            _manager.Pause(_item);
        else
            _manager.Start(_item);
        Refresh();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}

/// <summary>Draws where each connection started and how far it got.</summary>
public sealed class SegmentBar : FrameworkElement
{
    private static readonly Brush Background = new SolidColorBrush(Color.FromRgb(0xEE, 0xF2, 0xF7));
    private static readonly Brush Active = new SolidColorBrush(Color.FromRgb(0x1F, 0x6F, 0xD8));
    private static readonly Brush Done = new SolidColorBrush(Color.FromRgb(0x6F, 0xA8, 0xF0));
    private static readonly Pen Marker = new(new SolidColorBrush(Color.FromRgb(0x0B, 0x3D, 0x80)), 1);

    public DownloadItem? Item { get; set; }

    protected override void OnRender(DrawingContext dc)
    {
        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        dc.DrawRectangle(Background, null, bounds);
        if (Item is null)
            return;
        if (Item.Status == DownloadStatus.Completed)
        {
            dc.DrawRectangle(Done, null, bounds);
            return;
        }
        if (Item.Kind == DownloadKind.Torrent)
        {
            RenderTorrent(dc);
            return;
        }
        double total = Item.TotalSize;
        if (total <= 0)
            return;
        foreach (var s in Item.SnapshotSegments())
        {
            double x = s.Start / total * ActualWidth;
            double w = Math.Min(s.Downloaded, total - s.Start) / total * ActualWidth;
            dc.DrawRectangle(s.Active ? Active : Done, null, new Rect(x, 0, Math.Max(0, w), ActualHeight));
            dc.DrawLine(Marker, new Point(x, 0), new Point(x, ActualHeight));
        }
    }

    /// <summary>One block per selected file, side by side, filled as far as its verified pieces go.</summary>
    private void RenderTorrent(DrawingContext dc)
    {
        var files = Item!.SnapshotTorrentFiles().Where(f => f.Selected && f.Length > 0).ToList();
        double total = files.Sum(f => (double)f.Length);
        if (total <= 0)
            return;
        double x = 0;
        foreach (var f in files)
        {
            double w = f.Length / total * ActualWidth;
            dc.DrawRectangle(f.Downloaded >= f.Length ? Done : Active, null,
                new Rect(x, 0, Math.Min(f.Downloaded, f.Length) / (double)f.Length * w, ActualHeight));
            dc.DrawLine(Marker, new Point(x, 0), new Point(x, ActualHeight));
            x += w;
        }
    }
}
