using MailLoadTester;
using System.Diagnostics;
using Xunit;

namespace MailLoadTester.Tests;

public class RateLimiterTests
{
    [Fact]
    public async Task ZeroInterval_ReturnsImmediately()
    {
        var limiter = new RateLimiter(0);
        var sw = Stopwatch.StartNew();

        await limiter.WaitAsync(CancellationToken.None);

        Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(100));
    }

    [Fact]
    public async Task ConcurrentWaiters_AreGloballySpaced()
    {
        const int intervalMs = 40;
        var limiter = new RateLimiter(intervalMs);
        var times = new long[3];
        var sw = Stopwatch.StartNew();

        var tasks = Enumerable.Range(0, 3).Select(async i =>
        {
            await limiter.WaitAsync(CancellationToken.None);
            times[i] = sw.ElapsedMilliseconds;
        }).ToArray();

        await Task.WhenAll(tasks);
        Array.Sort(times);

        Assert.True(times[1] - times[0] >= intervalMs - 15,
            $"Second start was only {times[1] - times[0]} ms after the first.");
        Assert.True(times[2] - times[1] >= intervalMs - 15,
            $"Third start was only {times[2] - times[1]} ms after the second.");
    }

    [Fact]
    public async Task CancelledLastReservation_DoesNotLeavePhantomDelay()
    {
        var limiter = new RateLimiter(250);
        using var cts = new CancellationTokenSource();

        await limiter.WaitAsync(CancellationToken.None);
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => limiter.WaitAsync(cts.Token));

        var sw = Stopwatch.StartNew();
        await limiter.WaitAsync(CancellationToken.None);

        Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(100),
            $"Unexpected phantom delay: {sw.ElapsedMilliseconds} ms.");
    }

    [Fact]
    public async Task CancelledEarlierReservation_DoesNotMoveScheduleBackwards()
    {
        var limiter = new RateLimiter(100);
        using var first = new CancellationTokenSource();
        var firstWait = limiter.WaitAsync(first.Token);

        // Give the first waiter a chance to reserve the first slot.
        await Task.Delay(10);

        using var second = new CancellationTokenSource();
        var secondWait = limiter.WaitAsync(second.Token);
        second.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => secondWait);
        await firstWait;

        var sw = Stopwatch.StartNew();
        await limiter.WaitAsync(CancellationToken.None);
        Assert.True(sw.Elapsed >= TimeSpan.FromMilliseconds(50),
            $"Schedule was moved backwards after cancelling an earlier reservation: {sw.ElapsedMilliseconds} ms.");
    }
}
