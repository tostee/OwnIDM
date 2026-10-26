using System.Diagnostics;

namespace OwnIDM.Core.Engine;

/// <summary>
/// Token bucket shared by all connections of one download. Tokens may go negative
/// ("debt"), so chunks larger than the bucket still work: the caller just waits longer.
/// </summary>
public sealed class RateLimiter
{
    private readonly Func<long> _bytesPerSecond;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _lock = new();
    private double _tokens;
    private double _last;

    public RateLimiter(Func<long> bytesPerSecond) => _bytesPerSecond = bytesPerSecond;

    public async ValueTask WaitAsync(int bytes, CancellationToken ct)
    {
        long rate = _bytesPerSecond();
        if (rate <= 0)
            return;

        double delaySeconds;
        lock (_lock)
        {
            double now = _clock.Elapsed.TotalSeconds;
            _tokens = Math.Min(rate, _tokens + (now - _last) * rate);
            _last = now;
            _tokens -= bytes;
            if (_tokens >= 0)
                return;
            delaySeconds = -_tokens / rate;
        }
        await Task.Delay(TimeSpan.FromSeconds(delaySeconds), ct).ConfigureAwait(false);
    }
}
