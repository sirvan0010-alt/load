using Xunit;

namespace MailLoadTester.Tests;

public sealed class AttachmentPlannerTests
{
    [Fact]
    public void EnsurePreloadSafe_RejectsPayloadLargerThanHardCap()
    {
        var ex = Record.Exception(() =>
            AttachmentPlanner.EnsurePreloadSafe(513L * 1024 * 1024, "test payload"));
        Assert.NotNull(ex);
        Assert.IsType<InvalidOperationException>(ex);
    }

    [Fact]
    public void EnsurePreloadSafe_AcceptsTinyPayload()
    {
        var ex = Record.Exception(() => AttachmentPlanner.EnsurePreloadSafe(1024, "test payload"));
        Assert.Null(ex);
    }
}
