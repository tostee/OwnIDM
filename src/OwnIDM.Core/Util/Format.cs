using System.Globalization;

namespace OwnIDM.Core.Util;

public static class Format
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    public static string Bytes(long bytes)
    {
        if (bytes < 0)
            return "Desconocido";
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0
            ? $"{bytes} B"
            : value.ToString(value < 10 ? "0.00" : value < 100 ? "0.0" : "0", CultureInfo.CurrentCulture) + " " + Units[unit];
    }

    public static string Speed(double bytesPerSecond) =>
        bytesPerSecond <= 0 ? "" : Bytes((long)bytesPerSecond) + "/s";

    public static string Duration(TimeSpan? span)
    {
        if (span is not { } t || t.TotalSeconds < 0 || t.TotalDays > 7)
            return "";
        if (t.TotalHours >= 1)
            return $"{(int)t.TotalHours} h {t.Minutes} min";
        if (t.TotalMinutes >= 1)
            return $"{t.Minutes} min {t.Seconds} s";
        return $"{Math.Max(1, t.Seconds)} s";
    }
}

/// <summary>Expands "http://host/img*.jpg" style batch patterns, like IDM's batch download.</summary>
public static class BatchPattern
{
    public static List<string> Expand(string pattern, bool letters, string from, string to, int width)
    {
        int star = pattern.IndexOf('*');
        if (star < 0)
            throw new FormatException("El patrón debe contener un asterisco (*).");
        string head = pattern[..star], tail = pattern[(star + 1)..];
        var result = new List<string>();

        if (letters)
        {
            if (from.Length != 1 || to.Length != 1 || !char.IsLetter(from[0]) || !char.IsLetter(to[0]))
                throw new FormatException("Indica una sola letra en 'Desde' y 'Hasta'.");
            for (char c = from[0]; c <= to[0]; c++)
                result.Add(head + c + tail);
        }
        else
        {
            if (!int.TryParse(from, out int a) || !int.TryParse(to, out int b) || a < 0 || b < a)
                throw new FormatException("Indica un rango numérico válido.");
            if (b - a > 10000)
                throw new FormatException("Máximo 10 000 archivos por lote.");
            for (int i = a; i <= b; i++)
                result.Add(head + i.ToString(CultureInfo.InvariantCulture).PadLeft(width, '0') + tail);
        }
        return result;
    }
}
