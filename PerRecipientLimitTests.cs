using Xunit;

namespace MailLoadTester.Tests;

public sealed class PerRecipientLimitTests
{
    private static MailTestOptions Opts(int max) => new(
        From: "a@test.local",
        Recipients: new[] { "same@test.local" },
        SmtpHost: "127.0.0.1",
        Port: 25,
        Security: SmtpSecurity.None,
        UseAuthentication: false,
        Username: "",
        Password: "",
        MessageCount: 20,
        IntervalMs: 0,
        BatchMode: false,
        BatchSize: 1,
        BatchPauseSeconds: 0,
        MaxConcurrency: 10,
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
        EnableJitter: false,
        EnablePerRecipientLimit: true,
        MaxMessagesPerRecipient: max,
        PerRecipientWindowMinutes: 60,
        DetectGreylist: false,
        EnableBurstMode: false,
        EnableProgressiveBackoff: false,
        EnableWarmup: false,
        EnableSendingTimeWindow: false);

    [Fact]
    public void ConcurrentReserve_DoesNotExceedMax()
    {
        const int max = 3;
        var pace = new SmartPaceController(Opts(max));
        var ok = 0;
        Parallel.For(0, 40, _ =>
        {
            if (pace.TryReserveRecipient("same@test.local"))
                Interlocked.Increment(ref ok);
        });
        Assert.Equal(max, ok);

        // Release all
        for (int i = 0; i < max; i++)
            pace.ReleaseRecipient("same@test.local");

        Assert.True(pace.TryReserveRecipient("same@test.local"));
        pace.CommitRecipient("same@test.local");
    }

    [Fact]
    public void Commit_ConsumesReservation_AndCountsTowardLimit()
    {
        var pace = new SmartPaceController(Opts(1));
        Assert.True(pace.TryReserveRecipient("same@test.local"));
        pace.CommitRecipient("same@test.local");
        Assert.False(pace.TryReserveRecipient("same@test.local"));
    }
}

