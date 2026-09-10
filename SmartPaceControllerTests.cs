using System.Diagnostics;
using Xunit;

namespace MailLoadTester.Tests;

public sealed class SmartPaceControllerTests
{
    private static MailTestOptions BaseOptions(int intervalMs, bool jitter = false) => new(
        From: "a@test.local",
        Recipients: new[] { "b@test.local" },
        SmtpHost: "127.0.0.1",
        Port: 25,
        Security: SmtpSecurity.None,
        UseAuthentication: false,
        Username: "",
        Password: "",
        MessageCount: 10,
        IntervalMs: intervalMs,
        BatchMode: false,
        BatchSize: 1,
        BatchPauseSeconds: 0,
        MaxConcurrency: 5,
        Subject: "s",
        Body: "b",
        DisplayName: "n",
        RandomTestData: false,
        TestMode: false,
        AllowedDomains: "",
        HtmlBody: false,
        Attachments: Array.Empty<string>(),
        CustomHeaders: new Dictionary<string, string>(),
        IgnoreCertificateErrors: false,
        MaxRetries: 0,
        DryRun: true,
        EnableJitter: jitter,
        JitterPercent: 0,
        EnableBurstMode: false,
        EnableProgressiveBackoff: false,
        DetectGreylist: false,
        EnableWarmup: false,
        EnablePerRecipientLimit: false,
        EnableSendingTimeWindow: false);

    [Fact]
    public async Task GlobalSpacing_WithConcurrency_RespectsInterval()
    {
        // 5 paralelních workerů, interval 50 ms → 8 zpráv by mělo trvat ≥ ~7*50 ms
        const int intervalMs = 50;
        const int messages = 8;
        var pace = new SmartPaceController(BaseOptions(intervalMs));
        var sw = Stopwatch.StartNew();

        var tasks = Enumerable.Range(0, messages).Select(async _ =>
        {
            await pace.WaitBeforeSendAsync("b@test.local", CancellationToken.None);
            pace.RecordSuccess("b@test.local");
        });
        await Task.WhenAll(tasks);
        sw.Stop();

        // Dolní bound: (messages-1) * interval * 0.7 (tolerance na scheduler)
        var minExpected = TimeSpan.FromMilliseconds((messages - 1) * intervalMs * 0.7);
        Assert.True(sw.Elapsed >= minExpected,
            $"Elapsed {sw.Elapsed.TotalMilliseconds:F0} ms < expected min {minExpected.TotalMilliseconds:F0} ms — spacing není globální?");
    }

    [Fact]
    public async Task ZeroInterval_DoesNotBlock()
    {
        var pace = new SmartPaceController(BaseOptions(0));
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 20; i++)
            await pace.WaitBeforeSendAsync("x@test.local", CancellationToken.None);
        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(500));
    }

    [Fact]
    public async Task Cancellation_ReleasesWithoutHang()
    {
        var pace = new SmartPaceController(BaseOptions(5_000));
        using var cts = new CancellationTokenSource(100);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pace.WaitBeforeSendAsync("x@test.local", cts.Token));
    }
    [Fact]
    public async Task Cancellation_RemovesOnlyItsOwnReservation()
    {
        var pace = new SmartPaceController(BaseOptions(5_000));
        using var firstCts = new CancellationTokenSource();
        using var secondCts = new CancellationTokenSource();

        var first = pace.WaitBeforeSendAsync("first@test.local", firstCts.Token);
        var second = pace.WaitBeforeSendAsync("second@test.local", secondCts.Token);

        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
        while (pace.ScheduledReservationCount < 2 && Stopwatch.GetTimestamp() < deadline)
            await Task.Yield();

        Assert.Equal(2, pace.ScheduledReservationCount);

        firstCts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);

        // The second worker must keep its reservation. The buggy RemoveLast()
        // implementation removed the second worker's slot here.
        Assert.Equal(1, pace.ScheduledReservationCount);

        secondCts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        Assert.Equal(0, pace.ScheduledReservationCount);
    }

    [Fact]
    public async Task SuccessfulReservation_IsRemovedAfterWait()
    {
        var pace = new SmartPaceController(BaseOptions(20));

        await pace.WaitBeforeSendAsync("success@test.local", CancellationToken.None);

        Assert.Equal(0, pace.ScheduledReservationCount);
    }


    [Fact]
    public async Task PerRecipientLimit_DoesNotConsumeGlobalSlotWhileBlocked()
    {
        var pace = new SmartPaceController(Opts(1000));
        Assert.True(pace.TryReserveRecipient("same@test.local"));
        pace.CommitRecipient("same@test.local");

        using var cts = new CancellationTokenSource(120);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pace.WaitBeforeSendAsync("same@test.local", cts.Token));

        // The recipient was blocked before global pacing. No global slot may
        // be consumed merely by waiting for the recipient window.
        Assert.Equal(0, pace.ScheduledReservationCount);
    }

}
