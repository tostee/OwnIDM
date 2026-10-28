using System.Windows;
using OwnIDM.Core.Manager;
using OwnIDM.Core.Tools;
using OwnIDM.Core.Util;

namespace OwnIDM.App;

/// <summary>Shows what will be downloaded (versions, sizes) and installs it with progress.</summary>
public partial class ComponentsWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly DownloadManager _manager;
    private readonly IReadOnlyList<Tool> _tools;
    private readonly CancellationTokenSource _cts = new();
    private List<ToolRelease> _releases = [];
    private bool _installing;

    public ComponentsWindow(DownloadManager manager, IReadOnlyList<Tool> tools, string reason)
    {
        InitializeComponent();
        _manager = manager;
        _tools = tools;
        ReasonText.Text = reason;
        Loaded += async (_, _) => await ResolveAsync();
        Closing += (_, e) =>
        {
            // Closing mid-install would leave the dialog's caller without an answer: cancel first.
            if (_installing)
            {
                e.Cancel = true;
                _cts.Cancel();
            }
        };
        Closed += (_, _) => _cts.Cancel();
    }

    private static string Purpose(Tool tool) => tool switch
    {
        Tool.YtDlp => "descarga los vídeos de YouTube y otros sitios",
        Tool.Ffmpeg => "une vídeo y audio (calidades superiores a 360p, HLS/DASH)",
        _ => "lo necesita yt-dlp para YouTube",
    };

    private async Task ResolveAsync()
    {
        try
        {
            _releases = await _manager.Tools.ResolveAsync(_tools, _cts.Token);
            ListText.Text = string.Join("\n", _releases.Select(r =>
                $"• {ToolManager.Display(r.Tool)} {r.Version} ({Format.Bytes(r.Size)}): {Purpose(r.Tool)}" +
                (_manager.Tools.InstalledVersion(r.Tool) is { } v ? (v == r.Version ? " · ya instalado" : $" · actualiza la {v}") : "")));
            long total = _releases.Sum(r => r.Size);
            StatusText.Text = $"Total a descargar: {Format.Bytes(total)}";
            InstallButton.IsEnabled = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ListText.Text = "No se pudo consultar GitHub: " + ex.Message;
            StatusText.Text = "Comprueba la conexión a internet e inténtalo de nuevo.";
        }
    }

    private async void OnInstall(object sender, RoutedEventArgs e)
    {
        _installing = true;
        InstallButton.IsEnabled = false;
        Bar.Visibility = Visibility.Visible;
        var progress = new Progress<ToolProgress>(p =>
        {
            StatusText.Text = p.Message + (p.Total > 0 && p.Downloaded < p.Total
                ? $"  {Format.Bytes(p.Downloaded)} de {Format.Bytes(p.Total)}" : "");
            Bar.Value = p.Total > 0 ? Math.Min(100, p.Downloaded * 100.0 / p.Total) : 0;
        });
        try
        {
            await _manager.Tools.InstallAsync(_releases, progress, _cts.Token);
            _installing = false;
            DialogResult = true;
        }
        catch (OperationCanceledException)
        {
            _installing = false;
            Close();
        }
        catch (Exception ex)
        {
            _installing = false;
            Bar.Visibility = Visibility.Collapsed;
            StatusText.Text = "Error: " + ex.Message;
            StatusText.SetResourceReference(ForegroundProperty, "SystemFillColorCriticalBrush");
            InstallButton.Content = "Reintentar";
            InstallButton.IsEnabled = true;
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        if (_installing)
            _cts.Cancel(); // the install loop closes the window once it has stopped
        else
            Close();
    }

    /// <summary>Tools a yt-dlp download needs that are not available: yt-dlp, ffmpeg, a JavaScript runtime.</summary>
    public static List<Tool> MissingForYtDlp(Core.Settings.AppSettings settings)
    {
        var missing = new List<Tool>();
        if (Core.YtDlp.YtDlpTool.Find(settings) is null)
            missing.Add(Tool.YtDlp);
        if (Core.Hls.Ffmpeg.Find(settings) is null)
            missing.Add(Tool.Ffmpeg);
        if (Core.YtDlp.YtDlpTool.FindJsRuntime(settings) is null)
            missing.Add(Tool.Deno);
        return missing;
    }
}
