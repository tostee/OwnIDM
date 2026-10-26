using System.Windows;
using OwnIDM.Core.Manager;
using OwnIDM.Core.Update;
using OwnIDM.Core.Util;

namespace OwnIDM.App;

/// <summary>Offers a new version; on "Actualizar ahora" downloads and verifies its installer.</summary>
public partial class UpdateWindow : Window
{
    private readonly DownloadManager _manager;
    private readonly AppRelease _release;
    private readonly CancellationTokenSource _cts = new();
    private bool _downloading;

    public UpdateWindow(DownloadManager manager, AppRelease release)
    {
        InitializeComponent();
        _manager = manager;
        _release = release;
        HeadlineText.Text = $"Hay una versión nueva de OwnIDM: {release.Version} (tienes la {App.Updater.CurrentVersion}).";
        if (string.IsNullOrWhiteSpace(release.Notes))
            NotesBorder.Visibility = Visibility.Collapsed;
        else
            NotesText.Text = release.Notes.Trim();
        if (release.Size > 0)
            StatusText.Text = $"Tamaño de la descarga: {Format.Bytes(release.Size)}";
        Closing += (_, e) =>
        {
            if (_downloading)
            {
                e.Cancel = true;
                _cts.Cancel();
            }
        };
        Closed += (_, _) => _cts.Cancel();
    }

    /// <summary>The verified installer, once the dialog closes with true.</summary>
    public string? InstallerPath { get; private set; }

    private async void OnUpdate(object sender, RoutedEventArgs e)
    {
        _downloading = true;
        UpdateButton.IsEnabled = SkipButton.IsEnabled = false;
        LaterButton.Content = "Cancelar";
        Bar.Visibility = Visibility.Visible;
        StatusText.ClearValue(ForegroundProperty);
        var progress = new Progress<UpdateProgress>(p =>
        {
            StatusText.Text = p.Message + (p.Total > 0 && p.Downloaded < p.Total
                ? $"  {Format.Bytes(p.Downloaded)} de {Format.Bytes(p.Total)}" : "");
            Bar.Value = p.Total > 0 ? Math.Min(100, p.Downloaded * 100.0 / p.Total) : 0;
        });
        try
        {
            var path = await App.Updater.DownloadAsync(_release, progress, _cts.Token);
            StatusText.Text = "Comprobando la firma digital…";
            string? signError = null;
            await Task.Run(() => Authenticode.SameSignerAsThisApp(path, out signError));
            if (signError is not null)
                throw new IOException(signError);
            _downloading = false;
            InstallerPath = path;
            DialogResult = true;
        }
        catch (OperationCanceledException)
        {
            _downloading = false;
            Close();
        }
        catch (Exception ex)
        {
            _downloading = false;
            Bar.Visibility = Visibility.Collapsed;
            StatusText.Text = "Error: " + ex.Message;
            StatusText.Foreground = System.Windows.Media.Brushes.Firebrick;
            UpdateButton.Content = "Reintentar";
            UpdateButton.IsEnabled = SkipButton.IsEnabled = true;
            LaterButton.Content = "Más tarde";
        }
    }

    private void OnSkip(object sender, RoutedEventArgs e)
    {
        _manager.Settings.SkippedVersion = _release.Version.ToString();
        _manager.SaveSettings();
        Close();
    }

    private void OnLater(object sender, RoutedEventArgs e)
    {
        if (_downloading)
            _cts.Cancel(); // the download loop closes the window once it has stopped
        else
            Close();
    }
}
