using Xunit;

namespace MailLoadTester.Tests;

public sealed class BandwidthLimiterTests
{
    [Fact]
    public async Task LargePayload_DoesNotWaitForever_WhenPayloadExceedsOneSecondBucket()
    {
        // 8 kbps = 1024 bytes/s. A 2048-byte message used to deadlock logically
        // because the token bucket was capped at 1024 forever. Keep the test fast
        // while still exercising the >capacity path.
        var limiter = new MailLoadTester.BandwidthLimiter(8);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));

        await limiter.ThrottleAsync(2048, cts.Token);
    }
}
