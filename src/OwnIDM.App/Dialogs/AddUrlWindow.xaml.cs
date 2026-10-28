using System.Windows;

namespace OwnIDM.App;

public partial class AddUrlWindow : Wpf.Ui.Controls.FluentWindow
{
    public AddUrlWindow()
    {
        InitializeComponent();
        try
        {
            if (Clipboard.ContainsText() && IsValid(Clipboard.GetText().Trim()))
                UrlBox.Text = Clipboard.GetText().Trim();
        }
        catch (System.Runtime.InteropServices.COMException) { }
        UrlBox.SelectAll();
        OkButton.IsEnabled = IsValid(UrlBox.Text);
    }

    public string Url => UrlBox.Text.Trim();

    private static bool IsValid(string text) =>
        Uri.TryCreate(text, UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https") ||
        Core.Torrents.TorrentLink.MagnetHash(text) is not null;

    private void OnTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        bool ok = IsValid(Url);
        OkButton.IsEnabled = ok;
        Hint.Visibility = ok || Url.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;
}
