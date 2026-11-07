using System.Net;
using System.Net.Sockets;
using MonoTorrent;
using MonoTorrent.BEncoding;
using MonoTorrent.Connections.Dht;
using MonoTorrent.Dht;

namespace OwnIDM.Core.Torrents;

/// <summary>
/// MonoTorrent 3.0.2's DHT joins the network only through router.bittorrent.com, which no longer answers,
/// and it reads its node cache in a different format than it writes it, so the cache never helps either.
/// This wrapper starts it from the cached nodes (decoded properly) plus the routers that do answer.
/// </summary>
internal sealed class BootstrappedDhtEngine : IDhtEngine
{
    private static readonly (string Host, int Port)[] Routers =
    [
        ("dht.transmissionbt.com", 6881),
        ("dht.libtorrent.org", 25401),
        ("router.utorrent.com", 6881),
        ("router.bittorrent.com", 6881),
    ];

    private const int CompactNodeLength = 26; // 20-byte id + IPv4 address + port

    private readonly DhtEngine _inner = new();

    public event EventHandler<PeersFoundEventArgs> PeersFound
    {
        add => _inner.PeersFound += value;
        remove => _inner.PeersFound -= value;
    }

    public event EventHandler StateChanged
    {
        add => _inner.StateChanged += value;
        remove => _inner.StateChanged -= value;
    }

    public TimeSpan AnnounceInterval => _inner.AnnounceInterval;
    public bool Disposed => _inner.Disposed;
    public ITransferMonitor Monitor => _inner.Monitor;
    public TimeSpan MinimumAnnounceInterval => _inner.MinimumAnnounceInterval;
    public int NodeCount => _inner.NodeCount;
    public DhtState State => _inner.State;

    public void Add(IEnumerable<ReadOnlyMemory<byte>> nodes) => _inner.Add(nodes);
    public void Announce(InfoHash infoHash, int port) => _inner.Announce(infoHash, port);
    public void GetPeers(InfoHash infoHash) => _inner.GetPeers(infoHash);
    public Task<ReadOnlyMemory<byte>> SaveNodesAsync() => _inner.SaveNodesAsync();
    public Task SetListenerAsync(IDhtListener listener) => _inner.SetListenerAsync(listener);
    public Task StopAsync() => _inner.StopAsync();
    public void Dispose() => _inner.Dispose();

    public Task StartAsync() => StartAsync(ReadOnlyMemory<byte>.Empty);

    public async Task StartAsync(ReadOnlyMemory<byte> initialNodes)
    {
        using var nodes = new MemoryStream();
        foreach (var node in CachedNodes(initialNodes))
            nodes.Write(node.Span);
        foreach (var node in await RouterNodesAsync().ConfigureAwait(false))
            nodes.Write(node);
        // The engine reads its initial nodes as one run of compact nodes.
        await _inner.StartAsync(nodes.ToArray()).ConfigureAwait(false);
    }

    /// <summary>The cache as the engine saves it (a list of compact nodes), or raw compact nodes.</summary>
    internal static IEnumerable<ReadOnlyMemory<byte>> CachedNodes(ReadOnlyMemory<byte> data)
    {
        if (data.IsEmpty)
            return [];
        try
        {
            if (BEncodedValue.Decode(data.Span) is BEncodedList list)
                return list.OfType<BEncodedString>().Select(s => s.AsMemory()).Where(m => m.Length == CompactNodeLength).ToList();
        }
        catch (BEncodingException) { }
        return data.Length % CompactNodeLength == 0
            ? Enumerable.Range(0, data.Length / CompactNodeLength).Select(i => data.Slice(i * CompactNodeLength, CompactNodeLength)).ToList()
            : [];
    }

    /// <summary>The bootstrap routers as compact nodes; their real ids are learned from their replies.</summary>
    private static async Task<List<byte[]>> RouterNodesAsync()
    {
        var lookups = Routers.Select(async r =>
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var addresses = await Dns.GetHostAddressesAsync(r.Host, AddressFamily.InterNetwork, timeout.Token).ConfigureAwait(false);
                return addresses.Select(a => CompactNode(a, r.Port));
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                return [];
            }
        });
        return (await Task.WhenAll(lookups).ConfigureAwait(false)).SelectMany(n => n).ToList();
    }

    private static byte[] CompactNode(IPAddress address, int port)
    {
        var node = new byte[CompactNodeLength];
        Random.Shared.NextBytes(node.AsSpan(0, 20));
        address.GetAddressBytes().CopyTo(node, 20);
        node[24] = (byte)(port >> 8);
        node[25] = (byte)port;
        return node;
    }
}
