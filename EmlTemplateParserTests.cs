using System.Text;
using MimeKit;
using Xunit;

namespace MailLoadTester.Tests;

public sealed class EmlTemplateParserTests
{
    [Fact]
    public void Rejects_EmlFile_AboveSafetyLimit()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mlt-{Guid.NewGuid():N}.eml");
        try
        {
            File.WriteAllText(path, "From: a@example.test\r\nTo: b@example.test\r\nSubject: test\r\n\r\nhello");
            var ex = Assert.Throws<InvalidOperationException>(() =>
                EmlTemplateParser.Parse(path, maxFileBytes: 1, maxAttachmentBytes: 1024));
            Assert.Contains("příliš velká", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Rejects_Decoded_Attachment_AboveQuota()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mlt-{Guid.NewGuid():N}.eml");
        try
        {
            var message = new MimeMessage();
            message.From.Add(MailboxAddress.Parse("a@example.test"));
            message.To.Add(MailboxAddress.Parse("b@example.test"));
            message.Subject = "quota";
            var builder = new BodyBuilder { TextBody = "hello" };
            builder.Attachments.Add("large.txt", Encoding.UTF8.GetBytes(new string('X', 8192)));
            message.Body = builder.ToMessageBody();
            message.WriteTo(path);

            var ex = Assert.Throws<InvalidOperationException>(() =>
                EmlTemplateParser.Parse(path, maxFileBytes: 1024 * 1024, maxAttachmentBytes: 1024));
            Assert.Contains("bezpečný limit", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally { File.Delete(path); }
    }
}
