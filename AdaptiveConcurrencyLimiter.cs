namespace MailLoadTester;

/// <summary>
/// Adaptive concurrency gate: adjusts limit from recent error rate and grants
/// permits without busy-waiting (waiters are woken on Release / limit increase).
/// Thread-safe.
/// </summary>
public sealed class AdaptiveConcurrencyLimiter
{
    private readonly int _initial;
    private readonly int _min;
    private readonly int _max;
    private readonly object _lock = new();
    private int _current;
    private int _active;
    private int _windowAttempts;
    private int _windowErrors;
    private DateTime _windowStart = DateTime.UtcNow;
    private readonly TimeSpan _window = TimeSpan.FromSeconds(10);
    private readonly Queue<TaskCompletionSource<bool>> _waiters = new();

    public int Current => Volatile.Read(ref _current);
    public int Active => Volatile.Read(ref _active);

    public AdaptiveConcurrencyLimiter(int initial, int min = 1, int max = 20)
    {
        if (min < 1 || max < min) throw new ArgumentOutOfRangeException();
        _initial = Math.Clamp(initial, min, max);
        _min = min;
        _max = max;
        _current = _initial;
    }

    public Task AcquireAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        TaskCompletionSource<bool>? tcs = null;

        lock (_lock)
        {
            if (_active < _current)
            {
                _active++;
                return Task.CompletedTask;
            }

            tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Enqueue(tcs);
        }

        if (!ct.CanBeCanceled)
            return tcs.Task;

        return WaitWithCancellationAsync(tcs, ct);
    }

    private async Task WaitWithCancellationAsync(TaskCompletionSource<bool> tcs, CancellationToken ct)
    {
        await using var reg = ct.Register(static state =>
        {
            var (limiter, waiter) = ((AdaptiveConcurrencyLimiter, TaskCompletionSource<bool>))state!;
            limiter.TryCancelWaiter(waiter);
        }, (this, tcs));

        try
        {
            await tcs.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Permit was not granted.
            throw;
        }
    }

    private void TryCancelWaiter(TaskCompletionSource<bool> waiter)
    {
        lock (_lock)
        {
            // If already completed with a permit, leave _active alone — caller must Release().
            if (waiter.Task.IsCompleted) return;

            if (_waiters.Count > 0)
            {
                var kept = new Queue<TaskCompletionSource<bool>>(_waiters.Count);
                while (_waiters.Count > 0)
                {
                    var w = _waiters.Dequeue();
                    if (!ReferenceEquals(w, waiter))
                        kept.Enqueue(w);
                }
                while (kept.Count > 0)
                    _waiters.Enqueue(kept.Dequeue());
            }

            // Complete under the lock so Release cannot race TrySetResult vs cancel
            // on a dequeued-but-still-pending waiter.
            waiter.TrySetCanceled();
        }
    }

    public void Release()
    {
        lock (_lock)
        {
            if (_active > 0)
                _active--;

            // Hand permit directly to a waiter if under the current limit.
            // TrySetResult is called *inside* the lock — safe because the TCS was
            // created with RunContinuationsAsynchronously, so this can't block or
            // reenter. Doing it here (not after unlocking) closes a race where
            // TryCancelWaiter (fired from ct.Register on another thread) could see
            // the waiter already dequeued-but-not-yet-completed and race
            // TrySetCanceled() against this TrySetResult(true) — whichever lost
            // left the limiter's _active count permanently off by one, since the
            // caller only calls Release() back if AcquireAsync returned normally.
            while (_waiters.Count > 0 && _active < _current)
            {
                var next = _waiters.Dequeue();
                if (next.Task.IsCompleted) continue;
                _active++;
                next.TrySetResult(true);
                break;
            }
        }
    }

    public void RecordAttempt(bool success)
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            if (now - _windowStart > _window)
            {
                EvaluateWindow();
                _windowStart = now;
                _windowAttempts = 0;
                _windowErrors = 0;
            }
            _windowAttempts++;
            if (!success) _windowErrors++;

            // If limit increased, wake one waiter. See Release() for why
            // TrySetResult must happen inside the lock.
            while (_waiters.Count > 0 && _active < _current)
            {
                var woken = _waiters.Dequeue();
                if (woken.Task.IsCompleted) continue;
                _active++;
                woken.TrySetResult(true);
                break;
            }
        }
    }

    private void EvaluateWindow()
    {
        if (_windowAttempts < 5) return;
        var errorRate = (double)_windowErrors / _windowAttempts;
        if (errorRate > 0.3)
            _current = Math.Max(_min, _current / 2);
        else if (errorRate < 0.05 && _current < _max)
            _current = Math.Min(_max, _current + 1);
    }

    public void Reset()
    {
        Queue<TaskCompletionSource<bool>> cancel;
        lock (_lock)
        {
            _current = _initial;
            // _active must NOT be zeroed here — it tracks permits genuinely held by
            // workers that are still running (RentAsync succeeded, Release() not yet
            // called). Zeroing it while workers hold permits would let the gate
            // (`_active < _current`) admit far more concurrent workers than the
            // configured limit, and every subsequent Release() from an already-held
            // permit would then push _active negative-relative-to-reality (Release()
            // only decrements when > 0, so it silently stops decrementing once it
            // hits 0, permanently under-counting real concurrency from then on).
            // If _current ends up temporarily below _active after a reset, that's
            // fine and matches how the class already behaves after RecordAttempt()
            // shrinks _current below the live worker count — new admissions just
            // wait until enough Release() calls drain it back down.
            _windowAttempts = 0;
            _windowErrors = 0;
            _windowStart = DateTime.UtcNow;
            cancel = new Queue<TaskCompletionSource<bool>>(_waiters);
            _waiters.Clear();
        }
        while (cancel.Count > 0)
            cancel.Dequeue().TrySetCanceled();
    }
}
