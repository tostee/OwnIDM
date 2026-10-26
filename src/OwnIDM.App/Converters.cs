using System.Globalization;
using System.Windows.Data;
using OwnIDM.Core.Models;
using OwnIDM.Core.Util;

namespace OwnIDM.App;

public sealed class BytesConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is long b ? Format.Bytes(b) : "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class SpeedConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double s ? Format.Speed(s) : "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class DurationConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        Format.Duration(value as TimeSpan?);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Values: Status, Progress, TotalSize.</summary>
public sealed class StatusTextConverter : IMultiValueConverter
{
    public static string Describe(DownloadStatus status, double progress, long total) => status switch
    {
        DownloadStatus.Completed => "Completado",
        DownloadStatus.Error => "Error",
        DownloadStatus.Queued => "En cola",
        DownloadStatus.Connecting => "Conectando…",
        DownloadStatus.Paused => total > 0 && progress > 0 ? $"Pausado ({progress:0.0}%)" : "Pausado",
        DownloadStatus.Downloading => total > 0 ? $"{progress:0.0}%" : "Descargando…",
        _ => status.ToString(),
    };

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values is [DownloadStatus s, double p, long t] ? Describe(s, p, t) : "";

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class CategoryGlyphConverter : IValueConverter
{
    public static string For(string? category) => category switch
    {
        "Comprimidos" => "",
        "Documentos" => "",
        "Música" => "",
        "Programas" => "",
        "Vídeo" => "",
        _ => "",
    };

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => For(value as string);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
