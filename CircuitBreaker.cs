using System.Collections.Concurrent;

namespace MailLoadTester;

/// <summary>
/// Circuit breaker se dvěma režimy:
/// 1) Klasický – N po sobě jdoucích selhání stejné kategorie.
/// 2) Sliding window – chybovost v posledních N pokusech ≥ práh % (lockovaný ring buffer).
/// Thread-safe. Po otevření fail-fast do konce cooldownu.
/// </summary>
public sealed class CircuitBreaker
{
    private readonly int _threshold;
    private readonly TimeSpan _cooldown;
    private readonly ConcurrentDictionary<string, int> _failures = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastFailure = new();
    private readonly ConcurrentDictionary<string, DateTime> _openSince = new();

    private readonly int _windowSize;
    private readonly double _failureRatePercent;
    private readonly object _windowLock = new();
    private readonly bool[]? _ring;
    private int _ringIndex;
    private int _ringFilled;
    private int _windowFailures;
    private int _globalOpen;
    private int _everOpened;

    public bool IsTripped => Volatile.Read(ref _globalOpen) == 1;

    /// <summary>
    /// True if the breaker opened (any category or the sliding window) at any
    /// point during the run, even if it later closed again after cooldown.
    /// Used for final reporting, where a snapshot of the live _failures
    /// dictionary is misleading because RecordSuccess() clears it.
    /// </summary>
    public bool EverOpened => Volatile.Read(ref _everOpened) == 1;

    public CircuitBreaker(int threshold, TimeSpan? cooldown = null, int windowSize = 0, double failureRatePercent = 90.0)
    {
        _threshold = Math.Max(1, threshold);
        _cooldown = cooldown ?? TimeSpan.FromSeconds(30);
        _windowSize = Math.Max(0, windowSize);
        _failureRatePercent = Math.Clamp(failureRatePercent, 1.0, 100.0);
        if (_windowSize > 0)
            _ring = new bool[_windowSize];
    }

    public bool IsOpen(Exception ex)
    {
        if (IsTripped) return true;
        return IsKeyOpen(Classify(ex));
    }

    public bool IsAnyOpen(out string? openCategory)
    {
        if (Volatile.Read(ref _globalOpen) == 1)
        {
            lock (_windowLock)
            {
                // Re-validate under the same lock RecordWindow() uses, instead of
                // trusting the pre-lock snapshot of `since`. Without this, a
                // concurrent RecordWindow() that legitimately re-tripped the breaker
                // (or simply refreshed _openSince["sliding-window"] to "now") between
                // our outer check and acquiring the lock could have that brand-new
                // failure silently wiped out by this reset, which was really only
                // valid against the *stale* since-value read before the lock.
                if (_globalOpen == 1 && _openSince.TryGetValue("sliding-window", out var since) &&
                    DateTime.UtcNow - since >= _cooldown)
                {
                    // Cooldown is a real reset/half-open transition. Retaining the old
                    // ring would let one post-cooldown result immediately re-trip the
                    // breaker because the stale failures were still counted.
                    Array.Clear(_ring ?? Array.Empty<bool>());
                    _ringIndex = 0;
                    _ringFilled = 0;
                    _windowFailures = 0;
                    Interlocked.Exchange(ref _globalOpen, 0);
                    _openSince.TryRemove("sliding-window", out _);
                }
            }
        }

        if (IsTripped)
        {
            openCategory = "sliding-window";
            return true;
        }
        foreach (var key in _openSince.Keys.ToArray())
        {
            if (IsKeyOpen(key))
            {
                openCategory = key;
                return true;
            }
        }
        openCategory = null;
        return false;
    }

    bool IsKeyOpen(string key)
    {
        if (!_openSince.TryGetValue(key, out var since))
            return false;

        if (DateTime.UtcNow - since < _cooldown)
            return true;

        // Conditional removal is important here. A plain TryRemove(key) creates
        // a TOCTOU race: RecordFailure() may publish a fresh opening after the
        // snapshot above, but before this thread removes the entry. In that case
        // the old cooldown check would accidentally erase the fresh failure.
        var removed = ((ICollection<KeyValuePair<string, DateTime>>)_openSince)
            .Remove(new KeyValuePair<string, DateTime>(key, since));

        if (removed)
            _failures.TryRemove(key, out _);

        // If removal lost a race, the key now contains newer state. Report it as
        // open; the next check will evaluate the fresh timestamp normally.
        if (!removed && _openSince.TryGetValue(key, out var current))
            return DateTime.UtcNow - current < _cooldown;

        return false;
    }

    /// <summary>
    /// Úspěch vynuluje consecutive failure counters všech kategorií (globální „server ožil“).
    /// Sliding-window trip se tím okamžitě nezavírá — platí cooldown.
    /// </summary>
    public void RecordSuccess(Exception? ex = null)
    {
        _failures.Clear();
        foreach (var key in _openSince.Keys.ToArray())
        {
            if (key != "sliding-window")
                _openSince.TryRemove(key, out _);
        }
        RecordWindow(true);
    }

    public void RecordFailure(Exception ex)
    {
        var key = Classify(ex);
        var count = _failures.AddOrUpdate(key, 1, (_, c) => c + 1);
        _lastFailure[key] = DateTime.UtcNow;
        if (count >= _threshold)
        {
            _openSince[key] = DateTime.UtcNow;
            Volatile.Write(ref _everOpened, 1);
        }

        RecordWindow(false);
    }

    public void RecordResult(bool success)
    {
        if (success) RecordSuccess();
        else
        {
            var count = _failures.AddOrUpdate("general", 1, (_, c) => c + 1);
            if (count >= _threshold)
            {
                _openSince["general"] = DateTime.UtcNow;
                Volatile.Write(ref _everOpened, 1);
            }
            RecordWindow(false);
        }
    }

    void RecordWindow(bool success)
    {
        if (_ring is null || _windowSize <= 0) return;

        lock (_windowLock)
        {
            if (_ringFilled < _windowSize)
            {
                _ring[_ringIndex] = success;
                if (!success) _windowFailures++;
                _ringIndex = (_ringIndex + 1) % _windowSize;
                _ringFilled++;
            }
            else
            {
                var old = _ring[_ringIndex];
                if (!old) _windowFailures--;
                _ring[_ringIndex] = success;
                if (!success) _windowFailures++;
                _ringIndex = (_ringIndex + 1) % _windowSize;
            }

            if (_ringFilled >= _windowSize)
            {
                var rate = (100.0 * _windowFailures) / _windowSize;
                if (rate >= _failureRatePercent)
                {
                    Interlocked.Exchange(ref _globalOpen, 1);
                    Volatile.Write(ref _everOpened, 1);
                    _openSince["sliding-window"] = DateTime.UtcNow;
                }
            }
        }
    }

    public IReadOnlyDictionary<string, int> GetFailureCounts() => _failures;

    private static string Classify(Exception ex)
    {
        if (ex is MailKit.Net.Smtp.SmtpCommandException sce)
            return $"smtp_{(int)sce.StatusCode / 100}xx";
        if (ex is TimeoutException) return "timeout";
        if (ex is IOException) return "io";
        if (ex is MailKit.Security.AuthenticationException) return "auth";
        return ex.GetType().Name;
    }
}
