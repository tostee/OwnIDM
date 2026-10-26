using System.Windows;
using OwnIDM.Core.Util;

namespace OwnIDM.App;

public partial class BatchWindow : Window
{
    public BatchWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => OnChanged(this, new RoutedEventArgs());
    }

    public List<string> Urls { get; private set; } = [];
    public bool StartQueue => StartQueueBox.IsChecked == true;

    private void OnChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
            return;
        try
        {
            if (!Uri.TryCreate(PatternBox.Text.Trim().Replace("*", "0"), UriKind.Absolute, out var u) || !u.Scheme.StartsWith("http"))
                throw new FormatException("Introduce una dirección http(s) con un asterisco (*).");
            int width = int.TryParse(WidthBox.Text, out var w) ? Math.Clamp(w, 1, 10) : 1;
            Urls = BatchPattern.Expand(PatternBox.Text.Trim(), LettersRadio.IsChecked == true, FromBox.Text.Trim(), ToBox.Text.Trim(), width);
            PreviewText.Text = Urls.Count <= 3
                ? string.Join("\n", Urls)
                : $"{Urls[0]}\n{Urls[1]}\n…\n{Urls[^1]}\n\nTotal: {Urls.Count} archivos";
            PreviewText.Foreground = System.Windows.Media.Brushes.Black;
            OkButton.IsEnabled = true;
        }
        catch (FormatException ex)
        {
            Urls = [];
            PreviewText.Text = ex.Message;
            PreviewText.Foreground = System.Windows.Media.Brushes.Firebrick;
            OkButton.IsEnabled = false;
        }
    }

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = Urls.Count > 0;
}
