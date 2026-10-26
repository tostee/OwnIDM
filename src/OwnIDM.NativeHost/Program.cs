using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using OwnIDM.Core.Ipc;

// Chrome/Edge native messaging host: each message is a little-endian uint32 length + UTF-8 JSON.
// Messages are forwarded to the running OwnIDM app through its named pipe.

var stdin = Console.OpenStandardInput();
var stdout = Console.OpenStandardOutput();

while (await ReadMessageAsync(stdin) is { } json)
{
    IpcMessage? msg = null;
    try
    {
        msg = JsonSerializer.Deserialize<IpcMessage>(json, IpcProtocol.Json);
    }
    catch (JsonException) { }

    bool ok = msg switch
    {
        null => false,
        { Type: "ping" } => true,
        _ => await ForwardAsync(msg),
    };
    await WriteMessageAsync(stdout, JsonSerializer.Serialize(new { ok }));
}

// "ok" means the user accepted the download in OwnIDM, so the browser may cancel its copy.
// The wait stays under the ~5 min idle limit of the extension's service worker.
static async Task<bool> ForwardAsync(IpcMessage msg)
{
    var decisionTimeout = TimeSpan.FromMinutes(4);
    var result = await IpcClient.SendAsync(msg, decisionTimeout);
    if (result != IpcResult.Unavailable)
        return result == IpcResult.Accepted;

    // App not running: launch it and retry while it starts.
    var exe = Path.Combine(AppContext.BaseDirectory, "OwnIDM.exe");
    if (!File.Exists(exe))
        return false;
    Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
    for (int i = 0; i < 20 && result == IpcResult.Unavailable; i++)
    {
        await Task.Delay(500);
        result = await IpcClient.SendAsync(msg, decisionTimeout);
    }
    return result == IpcResult.Accepted;
}

static async Task<string?> ReadMessageAsync(Stream input)
{
    var header = new byte[4];
    if (!await ReadExactlyOrEofAsync(input, header))
        return null;
    int length = BinaryPrimitives.ReadInt32LittleEndian(header);
    if (length <= 0 || length > 1024 * 1024)
        return null;
    var body = new byte[length];
    return await ReadExactlyOrEofAsync(input, body) ? Encoding.UTF8.GetString(body) : null;
}

static async Task<bool> ReadExactlyOrEofAsync(Stream input, byte[] buffer)
{
    int read = 0;
    while (read < buffer.Length)
    {
        int n = await input.ReadAsync(buffer.AsMemory(read));
        if (n == 0)
            return false;
        read += n;
    }
    return true;
}

static async Task WriteMessageAsync(Stream output, string json)
{
    var body = Encoding.UTF8.GetBytes(json);
    var header = new byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);
    await output.WriteAsync(header);
    await output.WriteAsync(body);
    await output.FlushAsync();
}
