using MailLoadTester;
using Xunit;

namespace MailLoadTester.Tests;

public sealed class RandomTestDataTests
{
    [Fact]
    public void LegacyCreate_RemainsUsable()
    {
        var x = RandomTestData.Create(7);
        Assert.False(string.IsNullOrWhiteSpace(x.Name));
        Assert.Contains("#7", x.Subject);
        Assert.Contains("Test ID: 7", x.Body);
    }

    [Fact]
    public void BogusAndVariation_ProduceMessageSpecificMarker()
    {
        var a = RandomTestData.CreateContent(1, true, false, false, 2, true);
        var b = RandomTestData.CreateContent(2, true, false, false, 2, true);

        Assert.Contains("MLT-1-", a.Subject);
        Assert.Contains("MLT-2-", b.Subject);
        Assert.NotEqual(a.Subject, b.Subject);
        Assert.NotEqual(a.Body, b.Body);
    }

    [Fact]
    public void RandomHtml_ProducesHtmlBody()
    {
        var x = RandomTestData.CreateContent(1, false, true, false, 2, true);
        Assert.True(x.IsHtml);
        Assert.Contains("<!doctype html>", x.Body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MLT-1-", x.Subject);
    }

    [Fact]
    public void RandomAttachments_RespectMaximum()
    {
        for (var max = 1; max <= 5; max++)
        {
            var x = RandomTestData.CreateContent(1, false, false, true, max, true);
            Assert.InRange(x.Attachments.Count, 1, max);
            Assert.All(x.Attachments, a => Assert.NotEmpty(a.Content));
        }
    }

    [Fact]
    public void GeneratedAttachments_HaveExpectedFormats()
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < 100 && found.Count < 4; i++)
        {
            var x = RandomTestData.CreateContent(i, false, false, true, 5, true);
            foreach (var a in x.Attachments)
            {
                found.Add(Path.GetExtension(a.FileName));
                if (a.FileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                    Assert.True(a.Content.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71 }));
                if (a.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                    Assert.True(a.Content.AsSpan().StartsWith("%PDF-"u8));
            }
        }

        Assert.Contains(".txt", found);
        Assert.True(found.Any(x => x is ".png" or ".jpg" or ".pdf"));
    }

    [Fact]
    public void GeneratedAttachmentSize_IsExactForLargePayloads()
    {
        var x = RandomTestData.CreateContent(42, false, false, true, 5, true, randomAttachmentSizeMb: 1);
        Assert.NotEmpty(x.Attachments);
        Assert.All(x.Attachments, a => Assert.Equal(1024 * 1024, a.Content.Length));
    }

    [Fact]
    public void CreatePaddedPng_HandlesSmallTargetSizesWithoutOverrunning()
    {
        // Regression test: CreatePaddedPng used to guard only `dataLen < 2` before
        // unconditionally writing the 14-byte "MailLoadTester" keyword into the tEXt
        // chunk, which threw IndexOutOfRangeException for any target size that left
        // less than ~15 bytes of chunk-data budget. Not reachable via the GUI today
        // (min RandomAttachmentSizeMb is 1 MB) but must not crash if ever called with
        // a small size directly, e.g. from a future finer-grained size control.
        var method = typeof(RandomTestData).GetMethod("CreatePaddedPng",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);

        for (var target = 1; target <= 200; target++)
        {
            var result = (byte[])method!.Invoke(null, new object[] { target })!;
            Assert.NotEmpty(result);
            // Result is either the exact requested size, or the minimal 1x1 PNG
            // fallback when the requested size can't safely hold the tEXt chunk.
            Assert.True(result.Length == target || result.Length < target,
                $"Unexpected result length {result.Length} for target {target}");
            Assert.True(result.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71 }));
        }
    }

    [Fact]
    public async Task Generator_IsSafeUnderParallelUse()
    {
        var tasks = Enumerable.Range(1, 100).Select(i =>
            Task.Run(() => RandomTestData.CreateContent(i, true, true, true, 5, true)));

        var results = await Task.WhenAll(tasks);
        Assert.Equal(100, results.Length);
        Assert.Equal(100, results.Select(x => x.Subject).Distinct().Count());
        Assert.All(results, x => Assert.InRange(x.Attachments.Count, 1, 5));
    }
}


public sealed class RandomAttachmentSafetyTests
{
    [Fact]
    public void UnsafeHugeRequest_IsRejectedBeforeGeneration()
    {
        var estimate = AttachmentPlanner.EstimateRandomAttachments(1024, 5, 20);
        Assert.False(estimate.IsSafe);
        Assert.Contains("BLOKOVÁNO", estimate.Explanation);
    }

    [Fact]
    public void AutoModeProducesSafeEstimate()
    {
        var estimate = AttachmentPlanner.EstimateRandomAttachments(0, 2, 20);
        Assert.True(estimate.IsSafe);
        Assert.True(estimate.RequestedPerAttachmentBytes > 0);
        Assert.True(estimate.EstimatedTransientBytes >= estimate.WorstCaseBytes);
        Assert.True(estimate.EstimatedTransientBytes <= estimate.SafeBudgetBytes);
        Assert.InRange(AttachmentPlanner.ResolveRandomAttachmentSizeMb(0, 2, 20), 1, 128);
    }

    [Fact]
    public void AutoModeAccountsForRawAndMimeMemory()
    {
        var estimate = AttachmentPlanner.EstimateRandomAttachments(8, 5, 20);
        if (estimate.IsSafe)
            Assert.True(estimate.EstimatedTransientBytes <= estimate.SafeBudgetBytes);
    }

    [Fact]
    public void ManualReasonableRequest_IsAllowed()
    {
        var estimate = AttachmentPlanner.EstimateRandomAttachments(1, 2, 5);
        Assert.True(estimate.IsSafe);
    }
}
