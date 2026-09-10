namespace MailLoadTester;

using System.Diagnostics;

/// <summary>
/// Globální rate limiter s přesným plánováním startů.
/// Rezervace je zrušitelná bez zanechání "phantom delay" pro další waitery.
/// </summary>
public sealed class RateLimiter
{
    private readonly long _intervalTicks;
    private readonly object _lock = new();
    private readonly LinkedList<long> _schedule = new();

    public RateLimiter(int intervalMs)
    {
        _intervalTicks = intervalMs <= 0
            ? 0
            : (long)(intervalMs * Stopwatch.Frequency / 1000.0);
    }

    public async Task WaitAsync(CancellationToken ct)
    {
        if (_intervalTicks <= 0)
        {
            ct.ThrowIfCancellationRequested();
            return;
        }

        LinkedListNode<long> reservation;
        long reservedSlot;

        lock (_lock)
        {
            var now = Stopwatch.GetTimestamp();
            var nextAvailable = _schedule.Last?.Value ?? now;
            if (nextAvailable < now)
                nextAvailable = now;

            reservedSlot = nextAvailable + _intervalTicks;
            reservation = _schedule.AddLast(reservedSlot);

            // Odstraň již prošlé rezervace před naším slotem.
            while (_schedule.First is { } first && first != reservation &&
                   first.Value <= now)
                _schedule.RemoveFirst();
        }

        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();

                var remainingTicks = reservedSlot - Stopwatch.GetTimestamp();
                if (remainingTicks <= 0)
                    break;

                var delayMs = remainingTicks * 1000.0 / Stopwatch.Frequency;
                if (delayMs > 1)
                    await Task.Delay(TimeSpan.FromMilliseconds(delayMs), ct).ConfigureAwait(false);
                else
                    await Task.Yield();
            }
        }
        catch (OperationCanceledException)
        {
            lock (_lock)
            {
                // LinkedListNode umožňuje odstranit přesně naši rezervaci,
                // i když před ní čekají jiné workery.
                if (reservation.List is not null)
                    _schedule.Remove(reservation);
            }
            throw;
        }

        lock (_lock)
        {
            if (reservation.List is not null)
                _schedule.Remove(reservation);

            var now = Stopwatch.GetTimestamp();
            while (_schedule.First is { } first && first.Value <= now)
                _schedule.RemoveFirst();
        }
    }
}
