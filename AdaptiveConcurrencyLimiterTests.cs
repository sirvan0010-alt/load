using Xunit;

namespace MailLoadTester.Tests;

public sealed class AdaptiveConcurrencyLimiterTests
{
    [Fact]
    public async Task GateNeverExceedsCurrentLimit()
    {
        var limiter = new AdaptiveConcurrencyLimiter(2, 1, 4);
        var active = 0;
        var maxActive = 0;

        var tasks = Enumerable.Range(0, 8).Select(async _ =>
        {
            await limiter.AcquireAsync(CancellationToken.None);
            try
            {
                var now = Interlocked.Increment(ref active);
                maxActive = Math.Max(maxActive, now);
                await Task.Delay(20);
            }
            finally
            {
                Interlocked.Decrement(ref active);
                limiter.Release();
            }
        });

        await Task.WhenAll(tasks);
        Assert.InRange(maxActive, 1, 2);
        Assert.Equal(0, limiter.Active);
    }

    [Fact]
    public async Task CancellationWhileWaitingDoesNotLeakPermit()
    {
        var limiter = new AdaptiveConcurrencyLimiter(1, 1, 1);
        await limiter.AcquireAsync(CancellationToken.None);

        using var cts = new CancellationTokenSource(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => limiter.AcquireAsync(cts.Token));

        Assert.Equal(1, limiter.Active);
        limiter.Release();
        Assert.Equal(0, limiter.Active);
    }

    [Fact]
    public async Task ConcurrentReleaseAndCancellation_NeverLeaksOrDoublesPermits()
    {
        // Regression test: Release()/RecordAttempt() used to dequeue a waiter and
        // increment _active under the lock, but call TrySetResult(true) *after*
        // releasing the lock. If the waiter's CancellationToken fired in that exact
        // window, TryCancelWaiter() could race TrySetCanceled() against that
        // TrySetResult(true) on the same TaskCompletionSource. Whichever call lost
        // meant either a leaked permit (AcquireAsync threw, so the caller's
        // try/finally never called Release() for a permit that was already
        // counted as active) or, in principle, an inconsistency the other way.
        // Run many racy trials — with the bug this drifts `Active` away from 0
        // over iterations; with the fix it always nets back to exactly 0.
        for (var trial = 0; trial < 500; trial++)
        {
            var limiter = new AdaptiveConcurrencyLimiter(1, 1, 1);
            await limiter.AcquireAsync(CancellationToken.None); // hold the only permit

            using var cts = new CancellationTokenSource();
            var waiterTask = limiter.AcquireAsync(cts.Token);

            // Fire cancellation and release at (almost) the same time so the two
            // code paths genuinely race instead of one deterministically winning.
            var releaseTask = Task.Run(() => limiter.Release());
            var cancelTask = Task.Run(() => cts.Cancel());
            await Task.WhenAll(releaseTask, cancelTask);

            var waiterGotPermit = false;
            try
            {
                await waiterTask;
                waiterGotPermit = true;
            }
            catch (OperationCanceledException)
            {
                // Waiter lost the race — it must not hold a permit.
            }

            if (waiterGotPermit)
                limiter.Release();

            Assert.Equal(0, limiter.Active);
        }
    }

    [Fact]
    public async Task Reset_DoesNotZeroActiveWhilePermitsAreHeld()
    {
        // Regression test: Reset() used to set _active = 0 unconditionally, even
        // while workers genuinely held permits (RentAsync succeeded, Release() not
        // yet called). That let far more than the configured limit run concurrently
        // (the gate is `_active < _current`), and each subsequent Release() from an
        // already-live permit stopped decrementing once Active hit 0, permanently
        // under-counting concurrency from then on.
        var limiter = new AdaptiveConcurrencyLimiter(3, 1, 3);
        await limiter.AcquireAsync(CancellationToken.None);
        await limiter.AcquireAsync(CancellationToken.None);
        Assert.Equal(2, limiter.Active);

        limiter.Reset();

        Assert.Equal(2, limiter.Active); // still 2 genuinely held permits, not 0

        limiter.Release();
        limiter.Release();
        Assert.Equal(0, limiter.Active); // both releases correctly accounted for
    }
}
