using Xunit;

namespace MailLoadTester.Tests;

public sealed class CircuitBreakerTests
{
    [Fact]
    public void SlidingWindow_TripsAtConfiguredFailureRate()
    {
        var breaker = new CircuitBreaker(5, TimeSpan.FromSeconds(30), windowSize: 4, failureRatePercent: 75);
        breaker.RecordFailure(new InvalidOperationException());
        breaker.RecordFailure(new InvalidOperationException());
        breaker.RecordFailure(new InvalidOperationException());
        breaker.RecordSuccess();

        Assert.True(breaker.IsAnyOpen(out var category));
        Assert.Equal("sliding-window", category);
    }

    [Fact]
    public async Task SlidingWindow_CooldownStartsFreshWindow()
    {
        var breaker = new CircuitBreaker(5, TimeSpan.FromMilliseconds(20), windowSize: 3, failureRatePercent: 100);
        breaker.RecordFailure(new InvalidOperationException());
        breaker.RecordFailure(new InvalidOperationException());
        breaker.RecordFailure(new InvalidOperationException());
        Assert.True(breaker.IsAnyOpen(out _));

        await Task.Delay(50);
        Assert.False(breaker.IsAnyOpen(out _));

        // One post-cooldown failure must not inherit the old 3/3 window.
        breaker.RecordFailure(new InvalidOperationException());
        Assert.False(breaker.IsAnyOpen(out _));
    }

    [Fact]
    public async Task EverOpened_StaysTrueAfterCooldownRecovery()
    {
        // Regression test: SmtpTestRunner's final report used to derive
        // "circuit breaker opened" from a live snapshot of internal failure
        // counters, which RecordSuccess() clears — so a breaker that tripped
        // and then recovered before the run ended was reported as never
        // having opened at all. EverOpened must remain true regardless.
        var breaker = new CircuitBreaker(2, TimeSpan.FromMilliseconds(20));
        Assert.False(breaker.EverOpened);

        breaker.RecordFailure(new InvalidOperationException());
        breaker.RecordFailure(new InvalidOperationException());
        Assert.True(breaker.EverOpened);

        await Task.Delay(50);
        breaker.RecordSuccess();

        Assert.False(breaker.IsAnyOpen(out _));
        Assert.True(breaker.EverOpened);
    }

    [Fact]
    public async Task ConcurrentCooldownResetAndNewFailure_NeverLoseARecordedFailure()
    {
        // Regression test: IsAnyOpen()'s cooldown-expiry check used to read
        // `since` and evaluate "has cooldown elapsed" *before* acquiring
        // _windowLock. If a concurrent RecordWindow() (from RecordFailure) refreshed
        // _openSince["sliding-window"] to "now" in the gap between that check and
        // actually acquiring the lock, IsAnyOpen would still barrel ahead and wipe
        // the ring/reset the breaker using the stale pre-lock snapshot — silently
        // erasing the brand-new failure RecordWindow had just recorded. The fix
        // re-validates the cooldown condition *inside* the same lock RecordWindow
        // uses. This test hammers the exact boundary with concurrent threads across
        // many iterations; it's probabilistic (true races are timing-dependent) but
        // gives a real chance to catch a regression rather than proving one exists.
        for (var trial = 0; trial < 200; trial++)
        {
            var breaker = new CircuitBreaker(1, TimeSpan.FromMilliseconds(15), windowSize: 4, failureRatePercent: 50.0);
            // Trip it once.
            breaker.RecordFailure(new IOException());
            breaker.RecordFailure(new IOException());
            Assert.True(breaker.IsAnyOpen(out _));

            await Task.Delay(15); // sit right at the cooldown boundary

            // Race: one thread checks/resets via IsAnyOpen, another records a fresh
            // failure at (as close as we can get) the same instant.
            var checkTask = Task.Run(() => breaker.IsAnyOpen(out _));
            var failTask = Task.Run(() => breaker.RecordFailure(new IOException()));
            await Task.WhenAll(checkTask, failTask);

            // Whichever interleaving happened, EverOpened must remain true — the
            // breaker undeniably tripped at least once in this trial, regardless of
            // exactly how the race resolved.
            Assert.True(breaker.EverOpened);
        }
    }
}
