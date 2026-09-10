using Xunit;

namespace MailLoadTester.Tests;

public sealed class TemplateTagsTests
{
    [Fact]
    public void Replaces_Guid_Timestamp_TestId_RandomWord()
    {
        var input = "A {GUID} B {TIMESTAMP} C {TEST_ID} D {RANDOM_WORD:6} E {RANDOM_WORD}";
        var out1 = TemplateTags.Process(input, testId: 42);
        var out2 = TemplateTags.Process(input, testId: 42);

        Assert.DoesNotContain("{GUID}", out1, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("{TIMESTAMP}", out1, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("42", out1);
        Assert.DoesNotContain("{RANDOM_WORD", out1, StringComparison.OrdinalIgnoreCase);

        // GUID a random by měly být unikátní mezi voláními
        Assert.NotEqual(out1, out2);
    }

    [Fact]
    public void NullOrEmpty_Safe()
    {
        Assert.Equal("", TemplateTags.Process(null));
        Assert.Equal("", TemplateTags.Process(""));
        Assert.Equal("plain", TemplateTags.Process("plain"));
    }
}
