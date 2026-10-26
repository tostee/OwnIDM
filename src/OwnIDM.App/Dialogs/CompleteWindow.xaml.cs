using System.Windows;
using OwnIDM.Core.Manager;
using OwnIDM.Core.Models;
using OwnIDM.Core.Util;

namespace OwnIDM.App;

public partial class CompleteWindow : Window
{
    private readonly DownloadManager _manager;
    private readonly DownloadItem _item;

    public CompleteWindow(DownloadManager manager, DownloadItem item)
    {
        InitializeComponent();
        _manager = manager;
        _item = item;
        SizeText.Text = $"{item.FileName} · {Format.Bytes(item.TotalSize)}";
        PathText.Text = item.FullPath;
        UrlText.Text = item.Url;
        Closed += (_, _) =>
        {
            if (DontShow.IsChecked == true)
            {
                _manager.Settings.ShowCompleteDialog = false;
                _manager.SaveSettings();
            }
        };
    }

    private void OnOpen(object sender, RoutedEventArgs e)
    {
        Shell.Open(_item.FullPath);
        Close();
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        Shell.ShowInFolder(_item);
        Close();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
