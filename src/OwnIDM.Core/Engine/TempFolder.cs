using OwnIDM.Core.Models;
using OwnIDM.Core.Settings;

namespace OwnIDM.Core.Engine;

/// <summary>
/// Where a download keeps its temporary files. By default a hidden folder inside its destination folder:
/// the space comes from the disk the file is going to anyway (not the system drive), and finishing is a
/// rename instead of copying gigabytes across disks. The folder is pinned to the item when it starts, so
/// changing the settings or the destination later never loses what was already downloaded.
/// </summary>
public static class TempFolder
{
    /// <summary>Name of the hidden per-destination folder.</summary>
    public const string Name = "OwnIDM.tmp";

    /// <summary>The item's folder: the one it started with, else where it would start now.</summary>
    public static string For(AppSettings settings, DownloadItem item) =>
        !string.IsNullOrWhiteSpace(item.TempDirectory) ? item.TempDirectory
        : settings.TempInDownloadFolder && !string.IsNullOrWhiteSpace(item.SaveDirectory) ? Path.Combine(item.SaveDirectory, Name)
        : settings.TempDirectory;

    /// <summary>
    /// Pins the folder to the item and creates it. A destination that cannot hold it (read-only,
    /// missing drive) falls back to the general temporary folder.
    /// </summary>
    public static string Prepare(AppSettings settings, DownloadItem item)
    {
        var dir = For(settings, item);
        try
        {
            Create(dir);
        }
        catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) &&
                                   string.IsNullOrWhiteSpace(item.TempDirectory) && !SamePath(dir, settings.TempDirectory))
        {
            dir = settings.TempDirectory;
            Create(dir);
        }
        item.TempDirectory = dir;
        return dir;
    }

    /// <summary>Creates the folder (again: another download may have just removed it, empty); a per-destination one is hidden.</summary>
    public static void Create(string dir)
    {
        var info = Directory.CreateDirectory(dir);
        if (!IsPerDestination(dir) || info.Attributes.HasFlag(FileAttributes.Hidden))
            return;
        try
        {
            info.Attributes |= FileAttributes.Hidden;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Forgets the item's folder once its temporary files are gone (completed, removed, started over),
    /// and deletes it when it is a per-destination folder left empty.
    /// </summary>
    public static void Release(DownloadItem item)
    {
        var dir = item.TempDirectory;
        item.TempDirectory = null;
        if (dir is null || !IsPerDestination(dir))
            return;
        try
        {
            if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                Directory.Delete(dir);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static bool IsPerDestination(string dir) =>
        string.Equals(Path.GetFileName(Path.TrimEndingDirectorySeparator(dir)), Name, StringComparison.OrdinalIgnoreCase);

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
}
