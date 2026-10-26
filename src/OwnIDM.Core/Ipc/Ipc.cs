using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OwnIDM.Core.Ipc;

/// <summary>Message exchanged between the browser host / second instances and the running app.</summary>
public sealed class IpcMessage
{
    /// <summary>"add" (download a URL), "show" (bring the window up) or "ping".</summary>
    [JsonPropertyName("type")] public string Type { get; set; } = "add";
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("referrer")] public string? Referrer { get; set; }
    [JsonPropertyName("fileName")] public string? FileName { get; set; }
    [JsonPropertyName("cookies")] public string? Cookies { get; set; }
    [JsonPropertyName("userAgent")] public string? UserAgent { get; set; }
    [JsonPropertyName("fileSize")] public long? FileSize { get; set; }

    /// <summary>
    /// "hls" (.m3u8) or "dash" (.mpd) for a stream captured from a video player, "ytdlp" for a video
    /// page handed to yt-dlp; otherwise a plain file.
    /// </summary>
    [JsonPropertyName("kind")] public string? Kind { get; set; }
}

public static class IpcProtocol
{
    public static readonly string PipeName = "OwnIDM.Pipe." + Environment.UserName;

    public static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
}

public enum IpcResult
{
    Accepted,
    Declined,
    /// <summary>No OwnIDM instance is listening.</summary>
    Unavailable,
}

/// <summary>
/// Named pipe server restricted to the current user. Each connection carries one JSON line;
/// the reply ("ok" / "declined") is sent only once the handler has decided.
/// </summary>
public sealed class IpcServer : IDisposable
{
    private readonly Func<IpcMessage, Task<bool>> _handler;
    private readonly CancellationTokenSource _cts = new();

    public IpcServer(Func<IpcMessage, Task<bool>> handler) => _handler = handler;

    public void Start() => _ = Task.Run(() => AcceptLoopAsync(_cts.Token));

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = new NamedPipeServerStream(IpcProtocol.PipeName, PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(ct);
                _ = HandleAsync(server, ct); // connections are served concurrently
                server = null;
            }
            catch (OperationCanceledException)
            {
                server?.Dispose();
                return;
            }
            catch (Exception)
            {
                server?.Dispose();
                try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { return; }
            }
        }
    }

    private async Task HandleAsync(NamedPipeServerStream server, CancellationToken ct)
    {
        await using var _ = server;
        try
        {
            string? line;
            using (var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                readTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
                line = await reader.ReadLineAsync(readTimeout.Token);
            }
            var msg = line is null ? null : JsonSerializer.Deserialize<IpcMessage>(line, IpcProtocol.Json);
            bool ok = msg is not null && await _handler(msg);

            await using var writer = new StreamWriter(server, new UTF8Encoding(false), leaveOpen: true);
            await writer.WriteLineAsync(ok ? "ok" : "declined");
            await writer.FlushAsync(ct);
        }
        catch (Exception)
        {
            // Client went away, timed out or sent garbage: nothing to answer.
        }
    }

    public void Dispose() => _cts.Cancel();
}

public static class IpcClient
{
    /// <param name="responseTimeout">How long to wait for the app's decision once connected.</param>
    public static async Task<IpcResult> SendAsync(IpcMessage message, TimeSpan responseTimeout)
    {
        await using var client = new NamedPipeClientStream(".", IpcProtocol.PipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            using var connect = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));
            await client.ConnectAsync(connect.Token);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or TimeoutException or UnauthorizedAccessException)
        {
            return IpcResult.Unavailable;
        }

        try
        {
            using var cts = new CancellationTokenSource(responseTimeout);
            await using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true);
            await writer.WriteLineAsync(JsonSerializer.Serialize(message, IpcProtocol.Json));
            await writer.FlushAsync(cts.Token);

            using var reader = new StreamReader(client, Encoding.UTF8, leaveOpen: true);
            return await reader.ReadLineAsync(cts.Token) == "ok" ? IpcResult.Accepted : IpcResult.Declined;
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            // The app received the message but gave no answer in time: do not resend it.
            return IpcResult.Declined;
        }
    }
}
