using System.Diagnostics;

namespace MailLoadTester;

/// <summary>
/// Token-bucket omezovač propustnosti pro SMTP odesílání. Throttle se volá
/// s velikostí právě odeslané/odesílané zprávy (v bajtech) a asynchronně čeká,
/// dokud by odeslání nepřekročilo nastavený limit kbps.
/// </summary>
public sealed class BandwidthLimiter
{
    private readonly long _bytesPerSecond;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private long _tokens;
    private long _lastRefill;

    public BandwidthLimiter(int kbps)
    {
        _bytesPerSecond = kbps > 0 ? kbps * 1024L / 8 : long.MaxValue;
        _tokens = _bytesPerSecond;
        _lastRefill = Stopwatch.GetTimestamp();
    }

    public async Task ThrottleAsync(int bytes, CancellationToken ct)
    {
        if (_bytesPerSecond == long.MaxValue || bytes <= 0) return;

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Refill();
            // A token bucket whose capacity is exactly one second of bandwidth can
            // never admit a single message larger than that capacity: _tokens is capped
            // at _bytesPerSecond, so `_tokens < bytes` would remain true forever.
            // Allow the current message to define a larger temporary capacity. This
            // preserves the configured long-term rate while permitting one large MIME
            // payload to accumulate enough credit instead of spinning forever.
            var capacity = Math.Max(_bytesPerSecond, (long)bytes);
            while (_tokens < bytes)
            {
                var need = bytes - _tokens;
                var waitMsExact = need * 1000.0 / _bytesPerSecond;
                var chunkMs = double.IsInfinity(waitMsExact) || waitMsExact > MaxDelayChunkMs
                    ? MaxDelayChunkMs
                    : Math.Max(1, (int)waitMsExact);
                await Task.Delay(chunkMs, ct).ConfigureAwait(false);
                Refill(capacity);
            }
            _tokens -= bytes;
        }
        finally
        {
            _lock.Release();
        }
    }

    // Any single wait is capped to this and re-evaluated (Refill + recheck) rather
    // than computed as one huge Task.Delay — also keeps cancellation responsive
    // during a very long throttle wait instead of blocking in one giant delay.
    private const int MaxDelayChunkMs = 60_000;

    private void Refill(long? capacityOverride = null)
    {
        var now = Stopwatch.GetTimestamp();
        var elapsedSec = (now - _lastRefill) / (double)Stopwatch.Frequency;
        var add = (long)(elapsedSec * _bytesPerSecond);
        if (add > 0)
        {
            var capacity = capacityOverride ?? _bytesPerSecond;
            var newTokens = _tokens > long.MaxValue - add ? long.MaxValue : _tokens + add;
            _tokens = Math.Min(capacity, newTokens);
            _lastRefill = now;
        }
    }
}
