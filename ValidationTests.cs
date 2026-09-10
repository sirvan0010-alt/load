using Xunit;
using MailLoadTester;

namespace MailLoadTester.Tests;

public class ValidationTests
{
    static MailTestOptions Base(
        string from = "test@example.com",
        string[]? recipients = null,
        string host = "smtp.example.com",
        int port = 587,
        SmtpSecurity security = SmtpSecurity.StartTls,
        bool useAuth = false,
        string user = "",
        string pass = "",
        int count = 10,
        int interval = 0,
        bool batch = false,
        int batchSize = 5,
        int batchPause = 1,
        int concurrency = 1,
        string subject = "s",
        string body = "b",
        string name = "n",
        bool random = false,
        bool testMode = true,
        string domains = "example.com",
        bool html = false,
        string[]? attachments = null,
        IReadOnlyDictionary<string, string>? headers = null,
        bool ignoreCert = false,
        int retries = 1,
        bool dryRun = false)
        => new(from, recipients ?? new[] { "user@example.com" }, host, port, security, useAuth, user, pass,
            count, interval, batch, batchSize, batchPause, concurrency, subject, body, name, random, testMode, domains,
            html, attachments ?? Array.Empty<string>(), headers ?? new Dictionary<string, string>(), ignoreCert, retries, dryRun);

    [Theory]
    [InlineData("test@gmail.com")]
    [InlineData("abc.def+test@example.cz")]
    public void ValidEmailAccepted(string email) => Assert.True(Validation.IsValidEmail(email));

    [Theory]
    [InlineData("test")]
    [InlineData("@gmail.com")]
    [InlineData("")]
    public void InvalidEmailRejected(string email) => Assert.False(Validation.IsValidEmail(email));

    [Fact]
    public void RandomDataContainsTestId()
    {
        var x = RandomTestData.Create(42);
        Assert.Contains("42", x.Subject);
        Assert.Contains("42", x.Body);
    }

    [Fact]
    public void BatchModeRejectsZeroPause()
    {
        var o = Base(batch: true, batchPause: 0);
        Assert.Throws<ArgumentException>(() => Validation.Validate(o));
    }

    [Fact]
    public void TestModeRejectsUnauthorizedRecipientDomain()
    {
        var o = Base(recipients: new[] { "user@other.example" }, domains: "example.com");
        Assert.Throws<ArgumentException>(() => Validation.Validate(o));
    }

    [Fact]
    public void BatchModeAllowsFinalPartialBatch()
    {
        var o = Base(count: 12, batch: true, batchSize: 5, batchPause: 1);
        Validation.Validate(o);
    }

    [Fact]
    public void NonBatchModeDoesNotRequireBatchPause()
    {
        var o = Base(batch: false, batchSize: 0, batchPause: 0);
        Validation.Validate(o);
    }

    [Fact]
    public void AuthenticationRequiresPassword()
    {
        var o = Base(useAuth: true, user: "user", pass: "");
        Assert.Throws<ArgumentException>(() => Validation.Validate(o));
    }

    [Fact]
    public void MaxConcurrencyOutOfRangeRejected()
    {
        var o = Base(concurrency: 25);
        Assert.Throws<ArgumentException>(() => Validation.Validate(o));
    }

    [Fact]
    public void MaxRetriesOutOfRangeRejected()
    {
        var o = Base(retries: 6);
        Assert.Throws<ArgumentException>(() => Validation.Validate(o));
    }

    [Fact]
    public void MissingAttachmentFileRejected()
    {
        var o = Base(attachments: new[] { "C:\\nonexistent\\file.xyz" });
        Assert.Throws<ArgumentException>(() => Validation.Validate(o));
    }

    [Fact]
    public void ParseHeadersWorks()
    {
        var h = Validation.ParseHeaders("X-Test: 123\r\nX-Foo: bar");
        Assert.Equal("123", h["X-Test"]);
        Assert.Equal("bar", h["X-Foo"]);
    }

    [Fact]
    public void ImplicitTlsAccepted()
    {
        var o = Base(security: SmtpSecurity.ImplicitTls, port: 465);
        Validation.Validate(o);
    }

    [Fact]
    public void DryRunAccepted()
    {
        var o = Base(dryRun: true);
        Validation.Validate(o);
    }

    [Fact]
    public void StartTlsRejectsPort465()
    {
        var o = Base(port: 465, security: SmtpSecurity.StartTls);
        Assert.Throws<ArgumentException>(() => Validation.Validate(o));
    }

    [Fact]
    public void CustomHeadersMustBeXHeaders()
    {
        var o = Base(headers: new Dictionary<string, string> { ["Subject"] = "bad" });
        Assert.Throws<ArgumentException>(() => Validation.Validate(o));
    }

    [Fact]
    public void CustomHeaderRejectsNewlineInjection()
    {
        var o = Base(headers: new Dictionary<string, string> { ["X-Test"] = "ok\r\nBcc: bad@example.com" });
        Assert.Throws<ArgumentException>(() => Validation.Validate(o));
    }

    [Fact]
    public void ExplainSmtpErrorContainsHintForTimeout()
    {
        var msg = Validation.ExplainSmtpError(new TimeoutException("timed out"));
        Assert.Contains("časový limit", msg);
    }

    [Fact]
    public void ExplainSmtpErrorFallsBackToMessage()
    {
        var msg = Validation.ExplainSmtpError(new InvalidOperationException("custom failure"));
        Assert.Equal("custom failure", msg);
    }
}

public class AttachmentPlannerTests
{
    [Fact]
    public void EmptyAttachmentListProducesEmptyPlan()
    {
        var plan = AttachmentPlanner.Prepare(Array.Empty<string>(), 1);
        Assert.Empty(plan.Sources);
        Assert.Equal(0, plan.TotalBytes);
        Assert.True(plan.Preloaded);
    }

    [Fact]
    public void SmallAttachmentIsPreloaded()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mlt-{Guid.NewGuid():N}.bin");
        try
        {
            File.WriteAllBytes(path, new byte[1024]);
            var plan = AttachmentPlanner.Prepare(new[] { path }, 4);
            Assert.Single(plan.Sources);
            Assert.NotNull(plan.Sources[0].PreloadedContent);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void PlannerReportsAvailableMemory()
    {
        var plan = AttachmentPlanner.Prepare(Array.Empty<string>(), 1);
        Assert.True(plan.AvailableMemoryBytes > 0);
    }
}


public class AdditionalValidationTests
{
    [Fact]
    public void ImplicitTlsRequiresPort465()
    {
        var o = new MailTestOptions("test@example.com", new[] { "user@example.com" }, "smtp.example.com", 587,
            SmtpSecurity.ImplicitTls, false, "", "", 1, 0, false, 1, 1, 1, "s", "b", "n", false, true, "example.com",
            false, Array.Empty<string>(), new Dictionary<string,string>(), false, 0, true);
        Assert.Throws<ArgumentException>(() => Validation.Validate(o));
    }

    [Fact]
    public void CustomHeaderNameRejectsSpaces()
    {
        var o = new MailTestOptions("test@example.com", new[] { "user@example.com" }, "smtp.example.com", 587,
            SmtpSecurity.StartTls, false, "", "", 1, 0, false, 1, 1, 1, "s", "b", "n", false, true, "example.com",
            false, Array.Empty<string>(), new Dictionary<string,string>{{"X-Bad Header","1"}}, false, 0, true);
        Assert.Throws<ArgumentException>(() => Validation.Validate(o));
    }
}
