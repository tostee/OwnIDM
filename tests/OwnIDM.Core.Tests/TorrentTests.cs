using System.Net;
using System.Net.Sockets;
using MonoTorrent;
using MonoTorrent.Client;
using OwnIDM.Core.Models;
using OwnIDM.Core.Settings;
using OwnIDM.Core.Torrents;

namespace OwnIDM.Core.Tests;

public class TorrentLinkTests
{
    private const string Hash = "c12fe1c06bba254a9dc9f519b335aa7c1367a88a";

    [Fact]
    public void Recognises_magnet_links_and_torrent_addresses()
    {
        Assert.True(TorrentLink.IsMagnet($"  magnet:?xt=urn:btih:{Hash}"));
        Assert.False(TorrentLink.IsMagnet("https://example.com/magnet"));
        Assert.True(TorrentLink.IsTorrentUrl("https://example.com/files/linux.torrent?x=1"));
        Assert.True(TorrentLink.IsTorrentUrl("https://example.com/download.php?id=7", "linux.torrent"));
        Assert.False(TorrentLink.IsTorrentUrl("https://example.com/download.php?id=7"));
        Assert.False(TorrentLink.IsTorrentFile("relative.torrent"));
    }

    [Fact]
    public void Magnet_name_and_hash()
    {
        var magnet = $"magnet:?xt=urn:btih:{Hash.ToUpperInvariant()}&dn=Debian%2012%20netinst&tr=udp%3A%2F%2Ftracker.example%3A80";
        Assert.Equal(Hash, TorrentLink.MagnetHash(magnet));
        Assert.Equal("Debian 12 netinst", TorrentLink.DisplayName(magnet));
        Assert.Equal("torrent c12fe1c0", TorrentLink.DisplayName($"magnet:?xt=urn:btih:{Hash}"));
        Assert.Null(TorrentLink.MagnetHash("magnet:?dn=sin-hash"));
        Assert.Equal("ubuntu-24.04", TorrentLink.DisplayName("https://example.com/ubuntu-24.04.torrent"));
    }

    [Fact]
    public void Magnet_links_trigger_the_clipboard_monitor()
    {
        Assert.True(new AppSettings().IsMonitoredUrl($"magnet:?xt=urn:btih:{Hash}"));
    }

    [Fact]
    public void Category_follows_the_biggest_selected_file()
    {
        var files = new List<TorrentFileEntry>
        {
            new() { Path = "film.mkv", Length = 4_000_000_000 },
            new() { Path = "film.srt", Length = 80_000 },
            new() { Path = "extras.iso", Length = 9_000_000_000, Selected = false },
        };
        Assert.Equal("Vídeo", new AppSettings().CategoryFor(TorrentLink.MainFile(files)!));
    }
}

/// <summary>
/// Real transfers over loopback: a MonoTorrent seeder in the test and OwnIDM's downloader, with no
/// tracker, DHT or Internet; the downloader is told about the seeder directly.
/// </summary>
public sealed class TorrentDownloadTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ownidm-torrent-" + Guid.NewGuid().ToString("N"));
    private string SeedDir => Path.Combine(_root, "seed");
    private string DestDir => Path.Combine(_root, "dest");
    private ClientEngine _seeder = null!;
    private TorrentManager _seed = null!;
    private int _seedPort;
    private string _torrentPath = "";
    private Torrent _torrent = null!;

    public async Task InitializeAsync()
    {
        // A small multi-file torrent: "pack\a.bin", "pack\extra.bin", "pack\sub\b.bin", "pack\notes.txt".
        var pack = Path.Combine(SeedDir, "pack");
        Directory.CreateDirectory(Path.Combine(pack, "sub"));
        WriteRandom(Path.Combine(pack, "a.bin"), 3 * 1024 * 1024 + 123, seed: 1);
        WriteRandom(Path.Combine(pack, "sub", "b.bin"), 700 * 1024, seed: 2);
        WriteRandom(Path.Combine(pack, "extra.bin"), 2 * 1024 * 1024, seed: 3);
        File.WriteAllText(Path.Combine(pack, "notes.txt"), "léeme");

        var creator = new TorrentCreator(TorrentType.V1Only);
        var dict = await creator.CreateAsync(new TorrentFileSource(pack));
        _torrentPath = Path.Combine(_root, "pack.torrent");
        await File.WriteAllBytesAsync(_torrentPath, dict.Encode());
        _torrent = await Torrent.LoadAsync(_torrentPath);

        _seedPort = FreePort();
        _seeder = new ClientEngine(new EngineSettingsBuilder
        {
            CacheDirectory = Path.Combine(_root, "seed-cache"),
            AllowPortForwarding = false,
            AllowLocalPeerDiscovery = false,
            DhtEndPoint = null,
            ListenEndPoints = new() { ["ipv4"] = new IPEndPoint(IPAddress.Loopback, _seedPort) },
        }.ToSettings());
        _seed = await _seeder.AddAsync(_torrent, SeedDir);
        await _seed.StartAsync();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (_seed.State != TorrentState.Seeding && DateTime.UtcNow < deadline)
            await Task.Delay(100);
        Assert.Equal(TorrentState.Seeding, _seed.State);
    }

    public async Task DisposeAsync()
    {
        await _seed.StopAsync();
        _seeder.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private TorrentDownloader NewDownloader(AppSettings? settings = null)
    {
        settings ??= new AppSettings();
        settings.TorrentDirectory = Path.Combine(_root, "ownidm");
        settings.TorrentPort = FreePort();
        settings.TorrentDht = false;
        settings.TorrentPortForwarding = false;
        var downloader = new TorrentDownloader(settings);
        downloader.Started += m => _ = m.AddPeerAsync(new PeerInfo(new Uri($"ipv4://127.0.0.1:{_seedPort}")));
        return downloader;
    }

    [Fact]
    public async Task Downloads_the_selected_files_of_a_torrent_file()
    {
        using var downloader = NewDownloader();
        var probe = await downloader.ProbeAsync(_torrentPath, null, null, null, CancellationToken.None);
        Assert.Equal("pack", probe.Name);
        Assert.Equal(4, probe.Files.Count);
        Assert.True(File.Exists(downloader.MetadataPathFor(probe.InfoHash)));

        var item = new DownloadItem { Kind = DownloadKind.Torrent, Url = _torrentPath, InfoHash = probe.InfoHash, SaveDirectory = DestDir };
        item.TorrentFiles.AddRange(probe.Files);
        var notes = item.TorrentFiles.Single(f => f.Path.EndsWith("notes.txt"));
        notes.Selected = false;
        item.TorrentFiles.Single(f => f.Path.EndsWith("extra.bin")).Selected = false;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await downloader.RunAsync(item, cts.Token);

        Assert.True(item.Status == DownloadStatus.Completed, item.ErrorMessage);
        Assert.Equal("pack", item.FileName);
        long expected = probe.Files.Where(f => f.Selected).Sum(f => f.Length);
        Assert.Equal(expected, item.TotalSize);
        Assert.Equal(expected, item.Downloaded);
        AssertSameFile(Path.Combine(SeedDir, "pack", "a.bin"), Path.Combine(DestDir, "pack", "a.bin"));
        AssertSameFile(Path.Combine(SeedDir, "pack", "sub", "b.bin"), Path.Combine(DestDir, "pack", "sub", "b.bin"));
        // Pieces shared with it may have created part of the unwanted file: it is removed at the end.
        Assert.False(File.Exists(Path.Combine(DestDir, "pack", "extra.bin")));

        TorrentDownloader.DeleteDownloadedFiles(item);
        Assert.False(Directory.Exists(Path.Combine(DestDir, "pack")));
    }

    [Fact]
    public async Task Magnet_link_fetches_the_metadata_from_peers()
    {
        using var downloader = NewDownloader();
        var magnet = new MagnetLink(_torrent.InfoHashes, "pack", null, null, null).ToV1String();
        var item = new DownloadItem { Kind = DownloadKind.Torrent, Url = magnet, SaveDirectory = DestDir };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await downloader.RunAsync(item, cts.Token);

        Assert.True(item.Status == DownloadStatus.Completed, item.ErrorMessage);
        Assert.Equal(4, item.TorrentFiles.Count);
        Assert.Equal(_torrent.Size, item.Downloaded);
        Assert.Equal(TorrentLink.Hash(_torrent.InfoHashes), item.InfoHash);
        Assert.True(File.Exists(downloader.MetadataPathFor(item.InfoHash!)), "the metadata is kept for later runs");
        AssertSameFile(Path.Combine(SeedDir, "pack", "a.bin"), Path.Combine(DestDir, "pack", "a.bin"));
        Assert.Equal("léeme", File.ReadAllText(Path.Combine(DestDir, "pack", "notes.txt")));
    }

    [Fact]
    public async Task Pausing_keeps_the_data_and_resuming_finishes()
    {
        // The speed limiter keeps the first run slow enough to pause it half way.
        var settings = new AppSettings { SpeedLimiterEnabled = true, SpeedLimitKBps = 256 };
        using var downloader = NewDownloader(settings);
        var item = new DownloadItem { Kind = DownloadKind.Torrent, Url = _torrentPath, SaveDirectory = DestDir };

        using (var pause = new CancellationTokenSource())
        {
            void PauseSoon(TorrentManager _) => pause.CancelAfter(TimeSpan.FromSeconds(3));
            downloader.Started += PauseSoon;
            await downloader.RunAsync(item, pause.Token);
            downloader.Started -= PauseSoon;
        }
        Assert.Equal(DownloadStatus.Paused, item.Status);
        Assert.NotNull(item.InfoHash);
        Assert.InRange(item.Downloaded, 1, item.TotalSize - 1);

        settings.SpeedLimiterEnabled = false;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await downloader.RunAsync(item, cts.Token);
        Assert.True(item.Status == DownloadStatus.Completed, item.ErrorMessage);
        AssertSameFile(Path.Combine(SeedDir, "pack", "a.bin"), Path.Combine(DestDir, "pack", "a.bin"));
    }

    [Fact]
    public async Task Unselected_files_already_on_disk_are_never_deleted()
    {
        // The user has extra.bin from an earlier download and now only wants the rest.
        var mine = Path.Combine(DestDir, "pack", "extra.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(mine)!);
        File.Copy(Path.Combine(SeedDir, "pack", "extra.bin"), mine);

        using var downloader = NewDownloader();
        var probe = await downloader.ProbeAsync(_torrentPath, null, null, null, CancellationToken.None);
        var item = new DownloadItem { Kind = DownloadKind.Torrent, Url = _torrentPath, InfoHash = probe.InfoHash, SaveDirectory = DestDir };
        item.TorrentFiles.AddRange(probe.Files);
        item.TorrentFiles.Single(f => f.Path.EndsWith("extra.bin")).Selected = false;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await downloader.RunAsync(item, cts.Token);
        Assert.True(item.Status == DownloadStatus.Completed, item.ErrorMessage);
        AssertSameFile(Path.Combine(SeedDir, "pack", "extra.bin"), mine);

        // "Delete the files" takes what was downloaded and leaves the user's own file.
        TorrentDownloader.DeleteDownloadedFiles(item);
        Assert.False(File.Exists(Path.Combine(DestDir, "pack", "a.bin")));
        Assert.True(File.Exists(mine));
    }

    [Fact]
    public async Task Magnet_resumed_from_the_engine_cache_sets_up_its_files()
    {
        var settings = new AppSettings { SpeedLimiterEnabled = true, SpeedLimitKBps = 256 };
        using var downloader = NewDownloader(settings);
        var magnet = new MagnetLink(_torrent.InfoHashes, "pack", null, null, null).ToV1String();
        var item = new DownloadItem { Kind = DownloadKind.Torrent, Url = magnet, SaveDirectory = DestDir };
        using (var pause = new CancellationTokenSource())
        {
            void PauseSoon(TorrentManager _) => pause.CancelAfter(TimeSpan.FromSeconds(3));
            downloader.Started += PauseSoon;
            await downloader.RunAsync(item, pause.Token);
            downloader.Started -= PauseSoon;
        }
        Assert.Equal(DownloadStatus.Paused, item.Status);

        // OwnIDM's own copy of the metadata is gone; the engine still has it in its cache.
        File.Delete(downloader.MetadataPathFor(item.InfoHash!));
        lock (item.SyncRoot)
            item.TorrentFiles.Clear();

        settings.SpeedLimiterEnabled = false;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await downloader.RunAsync(item, cts.Token);
        Assert.True(item.Status == DownloadStatus.Completed, item.ErrorMessage);
        Assert.Equal(4, item.TorrentFiles.Count);
        Assert.Equal(_torrent.Size, item.Downloaded);
        AssertSameFile(Path.Combine(SeedDir, "pack", "a.bin"), Path.Combine(DestDir, "pack", "a.bin"));
    }

    [Fact]
    public async Task Hybrid_torrent_without_v2_piece_layers_downloads_with_its_v1_hashes()
    {
        Assert.True(TorrentDownloader.CanSkipV2Hashes, "the MonoTorrent internals used to skip the v2 hashes have changed");

        // The metadata a magnet link brings is the info dictionary alone: no v2 "piece layers". Neither side
        // has them, so no peer can ever send them; only the v1 hashes can verify the pieces.
        var seedDir = Path.Combine(_root, "hybrid-seed");
        Directory.CreateDirectory(seedDir);
        WriteRandom(Path.Combine(seedDir, "film.bin"), 2 * 1024 * 1024 + 77, seed: 4);
        var dict = await new TorrentCreator(TorrentType.V1V2Hybrid) { PieceLength = 16 * 1024 }
            .CreateAsync(new TorrentFileSource(Path.Combine(seedDir, "film.bin")));
        Assert.True(dict.Remove("piece layers"));
        var path = Path.Combine(_root, "hybrid.torrent");
        await File.WriteAllBytesAsync(path, dict.Encode());
        var torrent = await Torrent.LoadAsync(path);
        Assert.NotNull(torrent.InfoHashes.V1);
        Assert.NotNull(torrent.InfoHashes.V2);

        int port = FreePort();
        using var seeder = new ClientEngine(new EngineSettingsBuilder
        {
            CacheDirectory = Path.Combine(_root, "hybrid-seed-cache"),
            AllowPortForwarding = false,
            AllowLocalPeerDiscovery = false,
            DhtEndPoint = null,
            ListenEndPoints = new() { ["ipv4"] = new IPEndPoint(IPAddress.Loopback, port) },
        }.ToSettings());
        var seed = await seeder.AddAsync(torrent, seedDir);
        await seed.StartAsync();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (seed.State != TorrentState.Seeding && DateTime.UtcNow < deadline)
        {
            if (seed.State == TorrentState.FetchingHashes)
                TorrentDownloader.SkipV2Hashes(seeder, seed);
            await Task.Delay(100);
        }
        Assert.Equal(TorrentState.Seeding, seed.State);

        using var downloader = NewDownloader();
        downloader.Started += m => _ = m.AddPeerAsync(new PeerInfo(new Uri($"ipv4://127.0.0.1:{port}")));
        var item = new DownloadItem { Kind = DownloadKind.Torrent, Url = path, SaveDirectory = DestDir };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await downloader.RunAsync(item, cts.Token);
        await seed.StopAsync();

        Assert.True(item.Status == DownloadStatus.Completed, item.ErrorMessage);
        AssertSameFile(Path.Combine(seedDir, "film.bin"), Path.Combine(DestDir, "film.bin"));
    }

    [Fact]
    public async Task Invalid_torrent_file_is_reported()
    {
        var bogus = Path.Combine(_root, "bogus.torrent");
        await File.WriteAllTextAsync(bogus, "<html>not a torrent</html>");
        using var downloader = NewDownloader();
        await Assert.ThrowsAsync<InvalidDataException>(() => downloader.ProbeAsync(bogus, null, null, null, CancellationToken.None));

        var item = new DownloadItem { Kind = DownloadKind.Torrent, Url = bogus, SaveDirectory = DestDir };
        await downloader.RunAsync(item, CancellationToken.None);
        Assert.Equal(DownloadStatus.Error, item.Status);
        Assert.Contains("torrent", item.ErrorMessage);
    }

    private static void WriteRandom(string path, int length, int seed)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        File.WriteAllBytes(path, data);
    }

    private static void AssertSameFile(string expected, string actual)
    {
        Assert.True(File.Exists(actual), "missing " + actual);
        Assert.Equal(File.ReadAllBytes(expected), File.ReadAllBytes(actual));
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

public class BootstrappedDhtEngineTests
{
    private static byte[] Node(byte fill) => Enumerable.Repeat(fill, 26).ToArray();

    [Fact]
    public void Reads_the_node_cache_as_the_engine_saves_it()
    {
        // MonoTorrent saves a bencoded list of compact nodes...
        var saved = new MonoTorrent.BEncoding.BEncodedList
        {
            new MonoTorrent.BEncoding.BEncodedString(Node(1)),
            new MonoTorrent.BEncoding.BEncodedString(Node(2)),
            new MonoTorrent.BEncoding.BEncodedString(new byte[5]), // not a node
        }.Encode();
        var nodes = BootstrappedDhtEngine.CachedNodes(saved).Select(n => n.ToArray()).ToList();
        Assert.Equal([Node(1), Node(2)], nodes);
    }

    [Fact]
    public void Reads_raw_compact_nodes_and_ignores_garbage()
    {
        Assert.Equal(2, BootstrappedDhtEngine.CachedNodes(Node(1).Concat(Node(2)).ToArray()).Count());
        Assert.Empty(BootstrappedDhtEngine.CachedNodes(ReadOnlyMemory<byte>.Empty));
        Assert.Empty(BootstrappedDhtEngine.CachedNodes("le"u8.ToArray()));
        Assert.Empty(BootstrappedDhtEngine.CachedNodes(new byte[27]));
    }
}
