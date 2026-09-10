using System.Collections.Concurrent;
using System.Diagnostics;

namespace MailLoadTester;

/// <summary>
/// Chytrý kontroler tempa odesílání.
///
/// IntervalMs je GLOBÁLNÍ spacing (jako dřívější RateLimiter):
/// při 1000 ms a 20 workerech ≈ max 1 msg/s, ne 20 msg/s.
///
/// Navíc: jitter, burst+pause, progressive backoff, per-recipient,
/// časové okno, warm-up, greylist odklad.
/// Thread-safe.
/// </summary>
public sealed class SmartPaceController
{
    private readonly MailTestOptions _options;
    private readonly object _lock = new();
    private int _successCount;
    private int _messagesInCurrentBurst;
    private int _currentIntervalMs;
    private int _warmupPhaseIndex;
    private int _warmupMessagesInPhase;
    private readonly ConcurrentDictionary<string, RecipientWindow> _recipientWindows = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _burstPauseUntil = DateTimeOffset.MinValue;
    private DateTimeOffset _greylistPauseUntil = DateTimeOffset.MinValue;
    private int _greylistHits;

    // Globální fronta slotů (Stopwatch ticks) — jeden sdílený schedule pro všechny workery
    private readonly LinkedList<long> _schedule = new();
    private readonly long _freq = Stopwatch.Frequency;

    // A reservation keeps the exact LinkedList node belonging to one worker.
    // Cancellation must remove only that reservation, never an unrelated worker's slot.
    private readonly record struct GlobalReservation(
        TimeSpan Delay,
        string Reason,
        LinkedListNode<long>? Node);

    internal int ScheduledReservationCount
    {
        get { lock (_lock) return _schedule.Count; }
    }

    private sealed class RecipientWindow
    {
        /// <summary>Časy úspěšně odeslaných zpráv v aktuálním sliding window.</summary>
        public Queue<DateTimeOffset> Committed = new();
        /// <summary>Rezervace in-flight (mezi WaitBeforeSend a Commit/Release).</summary>
        public int Reserved;
    }

    private TimeSpan RecipientWindowSpan =>
        TimeSpan.FromMinutes(Math.Max(1, _options.PerRecipientWindowMinutes));

    private static void PurgeCommitted(RecipientWindow win, DateTimeOffset now, TimeSpan window)
    {
        while (win.Committed.Count > 0 && now - win.Committed.Peek() >= window)
            win.Committed.Dequeue();
    }

    public SmartPaceController(MailTestOptions options)
    {
        _options = options;
        _currentIntervalMs = Math.Max(0, options.IntervalMs);
    }

    public int CurrentIntervalMs
    {
        get { lock (_lock) return _currentIntervalMs; }
    }

    public string StatusText
    {
        get
        {
            lock (_lock)
            {
                var now = DateTimeOffset.UtcNow;
                if (now < _greylistPauseUntil)
                    return $"Greylist odklad – pokračuji v {_greylistPauseUntil.LocalDateTime:HH:mm:ss}";
                if (now < _burstPauseUntil)
                    return $"Pauza po burstu – pokračuji v {_burstPauseUntil.LocalDateTime:HH:mm:ss}";
                if (_options.EnableSendingTimeWindow && !IsInsideSendingWindow(now))
                    return "Čekám na časové okno odesílání";
                if (_options.EnableWarmup && _warmupPhaseIndex < GetWarmupPhases().Length)
                    return $"Warm-up fáze {_warmupPhaseIndex + 1}/{GetWarmupPhases().Length}";
                return "Běží";
            }
        }
    }

    /// <summary>
    /// Počká před odesláním. Nejdřív absolutní pauzy (greylist/burst/okno),
    /// pak globální slot pro interval (+ jitter).
    /// </summary>
    public async Task WaitBeforeSendAsync(string recipient, CancellationToken ct)
    {
        // 1) Absolutní blokace (sdílené pro všechny workery).
        //    Per-recipient limit se rezervuje PŘED globálním slotem.
        //    Jinak by worker mohl spotřebovat globální slot, potom dlouho čekat
        //    na recipient window a po jejím otevření odeslat bez nového
        //    globálního spacingu — výsledkem by mohl být burst.
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            TimeSpan block;
            lock (_lock)
            {
                block = ComputeAbsoluteBlock(recipient);
            }
            if (block <= TimeSpan.Zero)
                break;

            var slice = TimeSpan.FromMilliseconds(Math.Min(500, block.TotalMilliseconds));
            await Task.Delay(slice, ct).ConfigureAwait(false);
        }

        var recipientReserved = false;
        if (_options.EnablePerRecipientLimit)
        {
            while (!TryReserveRecipient(recipient))
            {
                ct.ThrowIfCancellationRequested();
                TimeSpan block;
                lock (_lock) { block = ComputeAbsoluteBlock(recipient); }
                if (block <= TimeSpan.Zero)
                    block = TimeSpan.FromMilliseconds(50);
                var slice = TimeSpan.FromMilliseconds(Math.Min(500, block.TotalMilliseconds));
                await Task.Delay(slice, ct).ConfigureAwait(false);
            }
            recipientReserved = true;
        }

        // 2) Globální spacing (IntervalMs ± jitter / warm-up).
        //    Pokud se čekání zruší, musí se vrátit i recipient reservation.
        try
        {
            var reservation = ReserveGlobalSlot();
            if (reservation.Delay > TimeSpan.Zero)
            {
                try
                {
                    var remaining = reservation.Delay;
                    while (remaining > TimeSpan.Zero)
                    {
                        ct.ThrowIfCancellationRequested();
                        var slice = TimeSpan.FromMilliseconds(Math.Min(500, remaining.TotalMilliseconds));
                        await Task.Delay(slice, ct).ConfigureAwait(false);
                        remaining -= slice;
                    }
                }
                finally
                {
                    // Cancellation i normální dokončení čekání odstraní přesně náš slot.
                    RemoveReservation(reservation.Node);
                }
            }
        }
        catch
        {
            if (recipientReserved)
                ReleaseRecipient(recipient);
            throw;
        }
    }

    /// <summary>
    /// Rezervuje další globální časový slot. Vrací zpoždění od teď do slotu
    /// a přesnou referenci na uzel této rezervace.
    /// </summary>
    private GlobalReservation ReserveGlobalSlot()
    {
        lock (_lock)
        {
            var intervalMs = _currentIntervalMs;

            // Warm-up: dočasně vyšší interval
            if (_options.EnableWarmup)
            {
                var phases = GetWarmupPhases();
                if (_warmupPhaseIndex < phases.Length && intervalMs > 0)
                {
                    // Rané fáze = pomalejší (násobek klesá)
                    var factor = 1.0 + (phases.Length - _warmupPhaseIndex) * 0.5;
                    intervalMs = (int)(intervalMs * factor);
                }
            }

            if (intervalMs <= 0)
                return new GlobalReservation(TimeSpan.Zero, "none", null);

            if (_options.EnableJitter && _options.JitterPercent > 0)
            {
                var jitter = _options.JitterPercent / 100.0;
                var factor = 1.0 + (Random.Shared.NextDouble() * 2 - 1) * jitter;
                intervalMs = (int)Math.Max(0, intervalMs * factor);
            }

            if (intervalMs <= 0)
                return new GlobalReservation(TimeSpan.Zero, "none", null);

            var intervalTicks = (long)(intervalMs * (double)_freq / 1000.0);
            var now = Stopwatch.GetTimestamp();
            var nextAvailable = _schedule.Last?.Value ?? now;
            if (nextAvailable < now)
                nextAvailable = now;

            var reservedSlot = nextAvailable + intervalTicks;
            var reservation = _schedule.AddLast(reservedSlot);

            // Úklid starých slotů
            while (_schedule.First is { } first && first.Value <= now && _schedule.Count > 1)
                _schedule.RemoveFirst();

            var waitTicks = reservedSlot - now;
            if (waitTicks <= 0)
                return new GlobalReservation(TimeSpan.Zero, "interval", reservation);

            var waitMs = waitTicks * 1000.0 / _freq;
            return new GlobalReservation(TimeSpan.FromMilliseconds(waitMs), "interval", reservation);
        }
    }

    private void CancelReservation(LinkedListNode<long>? reservation)
    {
        if (reservation is null)
            return;

        lock (_lock)
        {
            if (reservation.List == _schedule)
                _schedule.Remove(reservation);
        }
    }

    private void RemoveReservation(LinkedListNode<long> reservation)
    {
        lock (_lock)
        {
            if (reservation.List == _schedule)
                _schedule.Remove(reservation);
        }
    }

    private TimeSpan ComputeAbsoluteBlock(string recipient)
    {
        var now = DateTimeOffset.UtcNow;

        if (now < _greylistPauseUntil)
            return _greylistPauseUntil - now;

        if (now < _burstPauseUntil)
            return _burstPauseUntil - now;

        if (_options.EnableSendingTimeWindow && !IsInsideSendingWindow(now))
            return NextWindowStart(now) - now;

        if (_options.EnablePerRecipientLimit)
        {
            var key = recipient.Trim().ToLowerInvariant();
            if (_recipientWindows.TryGetValue(key, out var win))
            {
                PurgeCommitted(win, now, RecipientWindowSpan);
                if (win.Committed.Count + win.Reserved >= _options.MaxMessagesPerRecipient && win.Committed.Count > 0)
                    return win.Committed.Peek() + RecipientWindowSpan - now;
            }
        }

        return TimeSpan.Zero;
    }

    /// <summary>
    /// Rezervuje 1 slot pro příjemce (Count+Reserved &lt; Max). Volat po WaitBeforeSendAsync.
    /// false = limit plný (mělo by být vzácné, WaitBeforeSend už čeká).
    /// </summary>
    public bool TryReserveRecipient(string recipient)
    {
        if (!_options.EnablePerRecipientLimit) return true;
        var key = recipient.Trim().ToLowerInvariant();
        var now = DateTimeOffset.UtcNow;
        lock (_lock)
        {
            var win = _recipientWindows.GetOrAdd(key, _ => new RecipientWindow());
            PurgeCommitted(win, now, RecipientWindowSpan);
            if (win.Committed.Count + win.Reserved >= _options.MaxMessagesPerRecipient)
                return false;
            win.Reserved++;
            return true;
        }
    }

    /// <summary>Úspěšné odeslání: rezervace → commit (Count++).</summary>
    public void CommitRecipient(string recipient)
    {
        if (!_options.EnablePerRecipientLimit) return;
        var key = recipient.Trim().ToLowerInvariant();
        lock (_lock)
        {
            if (!_recipientWindows.TryGetValue(key, out var win)) return;
            var now = DateTimeOffset.UtcNow;
            PurgeCommitted(win, now, RecipientWindowSpan);
            if (win.Reserved > 0) win.Reserved--;
            win.Committed.Enqueue(now);
        }
    }

    /// <summary>Neúspěch / cancel: uvolní rezervaci bez navýšení Count.</summary>
    public void ReleaseRecipient(string recipient)
    {
        if (!_options.EnablePerRecipientLimit) return;
        var key = recipient.Trim().ToLowerInvariant();
        lock (_lock)
        {
            if (!_recipientWindows.TryGetValue(key, out var win)) return;
            if (win.Reserved > 0) win.Reserved--;
        }
    }

    public void RecordSuccess(string recipient)
    {
        lock (_lock)
        {
            _successCount++;
            _messagesInCurrentBurst++;

            // Per-recipient: Commit se volá explicitně z runneru (CommitRecipient).
            // Zde už jen burst/backoff/warmup.

            if (_options.EnableWarmup)
            {
                _warmupMessagesInPhase++;
                var phases = GetWarmupPhases();
                if (_warmupPhaseIndex < phases.Length && _warmupMessagesInPhase >= phases[_warmupPhaseIndex])
                {
                    _warmupPhaseIndex++;
                    _warmupMessagesInPhase = 0;
                }
            }

            if (_options.EnableProgressiveBackoff &&
                _options.BackoffAfterSuccesses > 0 &&
                _successCount % _options.BackoffAfterSuccesses == 0)
            {
                var next = (int)Math.Min(_options.MaxIntervalMs, _currentIntervalMs * _options.BackoffMultiplier);
                _currentIntervalMs = Math.Max(_currentIntervalMs, next);
            }

            if (_options.EnableBurstMode && _messagesInCurrentBurst >= _options.BurstSize)
            {
                _messagesInCurrentBurst = 0;
                _burstPauseUntil = DateTimeOffset.UtcNow.AddSeconds(Math.Max(1, _options.BurstPauseSeconds));
            }
        }
    }

    public void RecordGreylist()
    {
        if (!_options.DetectGreylist) return;
        lock (_lock)
        {
            // MaxGreylistRetries: 0 = unlimited; >0 = stop extending global pause after N hits
            if (_options.MaxGreylistRetries > 0 && _greylistHits >= _options.MaxGreylistRetries)
                return;
            _greylistHits++;
            _greylistPauseUntil = DateTimeOffset.UtcNow.AddMinutes(Math.Max(1, _options.GreylistRetryMinutes));
        }
    }

    public void RecordTransientThrottle()
    {
        lock (_lock)
        {
            if (_options.EnableProgressiveBackoff)
            {
                var next = (int)Math.Min(_options.MaxIntervalMs, _currentIntervalMs * Math.Max(1.2, _options.BackoffMultiplier));
                _currentIntervalMs = Math.Max(_currentIntervalMs, next);
            }
        }
    }

    private bool IsInsideSendingWindow(DateTimeOffset now)
    {
        var hour = now.ToLocalTime().Hour;
        var from = Math.Clamp(_options.SendingWindowFromHour, 0, 23);
        var to = Math.Clamp(_options.SendingWindowToHour, 0, 23);
        if (from == to) return true;
        if (from < to) return hour >= from && hour < to;
        return hour >= from || hour < to;
    }

    private DateTimeOffset NextWindowStart(DateTimeOffset now)
    {
        var local = now.ToLocalTime();
        var from = Math.Clamp(_options.SendingWindowFromHour, 0, 23);
        var candidate = new DateTimeOffset(local.Year, local.Month, local.Day, from, 0, 0, local.Offset);
        if (candidate <= local)
            candidate = candidate.AddDays(1);
        return candidate.ToUniversalTime();
    }

    private int[]? _warmupPhasesCache;

    private int[] GetWarmupPhases()
    {
        // _options.WarmupPhases never changes after construction, but this used to
        // be re-parsed (Split + LINQ) on every single call — and ReserveGlobalSlot()
        // calls it once per message for the whole warm-up phase. Cache it once.
        return _warmupPhasesCache ??= ParseWarmupPhases();
    }

    private int[] ParseWarmupPhases()
    {
        if (string.IsNullOrWhiteSpace(_options.WarmupPhases))
            return new[] { 50, 150, 500 };
        return _options.WarmupPhases
            .Split(new[] { ';', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => int.TryParse(s, out var n) ? n : 0)
            .Where(n => n > 0)
            .DefaultIfEmpty(50)
            .ToArray();
    }
}
