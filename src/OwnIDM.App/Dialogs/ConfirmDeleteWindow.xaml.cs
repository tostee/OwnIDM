using System.Windows;
using OwnIDM.Core.Models;

namespace OwnIDM.App;

public partial class ConfirmDeleteWindow : Window
{
    public ConfirmDeleteWindow(IReadOnlyList<DownloadItem> items)
    {
        InitializeComponent();
        int unfinished = items.Count(i => i.Status != DownloadStatus.Completed);
        MessageText.Text = (items.Count == 1
                               ? $"¿Quitar «{items[0].FileName}» de la lista de descargas?"
                               : $"¿Quitar {items.Count} descargas de la lista?") +
                           (unfinished > 0 ? "\n\nLas descargas sin terminar perderán lo descargado hasta ahora." : "");
        DeleteFilesBox.Visibility = items.Any(i => i.Status == DownloadStatus.Completed)
            ? Visibility.Visible : Visibility.Collapsed;
    }

    public bool DeleteFiles => DeleteFilesBox.IsChecked == true;

    private void OnYes(object sender, RoutedEventArgs e) => DialogResult = true;
}
