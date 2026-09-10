using Bogus;
using System.Text;

namespace MailLoadTester;

/// <summary>
/// Central generator for synthetic message content.
///
/// MAINTENANCE CONTRACT (important for future AI/code changes):
/// - Never change the user-supplied From address here.
/// - Every generated message must contain a stable MailLoadTester test ID.
/// - Generated attachments are small by default and are created per-message. User-requested
///   larger sizes are allowed only after AttachmentPlanner's safety preflight.
/// - The runner is responsible for combining these per-message attachments with
///   the AttachmentPlanner's user-supplied attachments.
/// - MaxRandomAttachments is a hard per-message upper bound (1..5).
/// - RandomAttachmentSizeMb is 0=Auto or a user-requested size. The runner must call
///   AttachmentPlanner.EstimateRandomAttachments before generating large payloads; unsafe
///   settings must be rejected before allocating large byte arrays.
/// - Generated content is intentionally randomized; the embedded test ID/token make a
///   failed message traceable, but the generator is not deterministic across runs.
/// - Legacy Create(int id) must remain compatible.
/// </summary>
public static class RandomTestData
{
    private static readonly string[] Names =
        ["Test User", "QA Tester", "SMTP Test", "Load Test", "Mail QA"];

    private static readonly string[] Subjects =
        ["SMTP test", "Delivery test", "Load test message", "Mail pipeline test", "Automated QA message"];

    private static readonly string[] Bodies =
    [
        "Automaticky generovaná testovací zpráva. Slouží k ověření SMTP pipeline.",
        "Testovací zpráva MailLoadTester. Neobsahuje produkční data.",
        "SMTP delivery/load test – generated test payload.",
        "Automated mail test message. Test ID bude přidán při odeslání."
    ];

    private static readonly string[] AttachmentExtensions = [".txt", ".png", ".jpg", ".pdf"];

    // Valid 1x1 image/PDF payloads. Small by design so they do not bypass AttachmentPlanner
    // or cause avoidable RAM pressure during high-concurrency tests.
    private static readonly byte[] Png1x1 = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
    private static readonly byte[] Jpeg1x1 = Convert.FromBase64String(
        "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAP//////////////////////////////////////////////////////////////////////////////////////2wBDAf//////////////////////////////////////////////////////////////////////////////////////wAARCAABAAEDASIAAhEBAxEB/8QAFQABAQAAAAAAAAAAAAAAAAAAAAf/xAAUEAEAAAAAAAAAAAAAAAAAAAAA/9oADAMBAAIQAxAAAAH/AP/EABQQAQAAAAAAAAAAAAAAAAAAACD/2gAIAQEAAT8Af//Z");
    private static readonly byte[] PdfMinimal = Encoding.ASCII.GetBytes(
        "%PDF-1.4\n1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n2 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\ntrailer\n<< /Root 1 0 R >>\n%%EOF\n");

    public sealed record GeneratedAttachment(string FileName, byte[] Content);

    public sealed record GeneratedMessageContent(
        string Name,
        string Subject,
        string Body,
        bool IsHtml,
        IReadOnlyList<GeneratedAttachment> Attachments);

    /// <summary>Legacy API preserved for existing callers and tests.</summary>
    public static (string Name, string Subject, string Body) Create(int id)
    {
        var generated = CreateContent(id, useBogusData: false, generateRandomHtml: false,
            generateRandomAttachments: false, maxRandomAttachments: 0,
            varySubjectBodyPerMessage: true);
        return (generated.Name, generated.Subject, generated.Body);
    }

    public static GeneratedMessageContent CreateContent(
        int id,
        bool useBogusData,
        bool generateRandomHtml,
        bool generateRandomAttachments,
        int maxRandomAttachments,
        bool varySubjectBodyPerMessage,
        int randomAttachmentSizeMb = 0)
    {
        maxRandomAttachments = Math.Clamp(maxRandomAttachments, 1, 5);
        var token = Guid.NewGuid().ToString("N")[..12];

        string name;
        string subject;
        string body;
        Faker? faker = null;

        if (useBogusData)
        {
            // Mix Czech and English locales per message so "realistic data" really
            // exercises both language/content variants instead of silently being EN-only.
            faker = new Faker(Random.Shared.Next(2) == 0 ? "cs" : "en");
            name = faker.Name.FullName();
            subject = faker.Commerce.ProductName() + " – test";
            body = string.Join("\r\n\r\n",
                faker.Lorem.Sentences(Random.Shared.Next(2, 5)),
                $"{faker.Company.CompanyName()}",
                $"Reference: {faker.Random.Word()}",
                $"Generated for MailLoadTester test #{id}.");
        }
        else
        {
            name = Names[Random.Shared.Next(Names.Length)];
            subject = Subjects[Random.Shared.Next(Subjects.Length)];
            body = Bodies[Random.Shared.Next(Bodies.Length)];
        }

        if (varySubjectBodyPerMessage || useBogusData)
        {
            subject = $"{subject} [MLT-{id}-{token}]";
            body += $"\r\n\r\nTest ID: {id}\r\nRandom token: {token}\r\nUTC: {DateTime.UtcNow:O}";
        }
        else
        {
            subject = $"{subject} #{id}";
            body += $"\r\n\r\nTest ID: {id}\r\nRandom token: {token}\r\nUTC: {DateTime.UtcNow:O}";
        }

        var isHtml = generateRandomHtml;
        if (isHtml)
        {
            var safeName = System.Net.WebUtility.HtmlEncode(name);
            var safeSubject = System.Net.WebUtility.HtmlEncode(subject);
            var safeBody = System.Net.WebUtility.HtmlEncode(body).Replace("\r\n", "<br>\r\n");
            body = $"""
                <!doctype html>
                <html><body style="font-family:Arial,sans-serif;margin:0">
                <div style="padding:18px;background:linear-gradient(90deg,#245,#578);color:white">
                  <h2 style="margin:0">{safeSubject}</h2>
                </div>
                <div style="padding:18px">
                  <p>Hello {safeName},</p>
                  <p>{safeBody}</p>
                  <p><a href="https://example.invalid/mail-load-test?id={id}">Test link</a></p>
                </div>
                </body></html>
                """;
        }

        var attachments = new List<GeneratedAttachment>();
        if (generateRandomAttachments)
        {
            var count = Random.Shared.Next(1, maxRandomAttachments + 1);
            var shuffled = AttachmentExtensions.OrderBy(_ => Random.Shared.Next()).Take(count).ToArray();
            foreach (var ext in shuffled)
            {
                var targetBytes = ResolveAttachmentSizeBytes(randomAttachmentSizeMb, ext);
                var bytes = CreateAttachmentBytes(ext, targetBytes, id, token);
                attachments.Add(new GeneratedAttachment($"mlt-{id}-{token}{ext}", bytes));
            }
        }

        return new GeneratedMessageContent(name, subject, body, isHtml, attachments);
    }

    private static long ResolveAttachmentSizeBytes(int requestedMb, string extension)
    {
        if (requestedMb <= 0)
            return extension switch { ".txt" => 512, ".pdf" => PdfMinimal.Length, ".png" => Png1x1.Length, ".jpg" => Jpeg1x1.Length, _ => 512 };
        return checked((long)requestedMb * 1024 * 1024);
    }

    private static byte[] CreateAttachmentBytes(string extension, long targetBytes, int id, string token)
    {
        if (targetBytes > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(targetBytes), "Generovaná příloha je příliš velká pro jeden byte[] payload.");
        if (extension == ".png" && targetBytes == Png1x1.Length) return Png1x1.ToArray();
        if (extension == ".jpg" && targetBytes == Jpeg1x1.Length) return Jpeg1x1.ToArray();
        if (extension == ".pdf" && targetBytes == PdfMinimal.Length) return PdfMinimal.ToArray();
        if (extension == ".png") return CreatePaddedPng((int)targetBytes);
        if (extension == ".jpg") return CreatePaddedJpeg((int)targetBytes);
        if (extension == ".pdf") return CreatePaddedPdf((int)targetBytes);

        var bytes = new byte[(int)targetBytes];
        var header = Encoding.UTF8.GetBytes($"MailLoadTester synthetic attachment\r\nTest ID: {id}\r\nToken: {token}\r\n");
        Buffer.BlockCopy(header, 0, bytes, 0, Math.Min(header.Length, bytes.Length));
        for (int i = header.Length; i < bytes.Length; i++) bytes[i] = (byte)((i * 31 + id) & 0xFF);
        return bytes;
    }

    private static byte[] CreatePaddedPdf(int targetBytes)
    {
        var baseText = Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n2 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\ntrailer\n<< /Root 1 0 R >>\n");
        var eof = Encoding.ASCII.GetBytes("%%EOF\n");
        if (targetBytes <= baseText.Length + eof.Length) return PdfMinimal.ToArray();
        var result = new byte[targetBytes];
        Buffer.BlockCopy(baseText, 0, result, 0, baseText.Length);
        Array.Fill(result, (byte)' ', baseText.Length, targetBytes - baseText.Length - eof.Length);
        Buffer.BlockCopy(eof, 0, result, targetBytes - eof.Length, eof.Length);
        return result;
    }

    private static byte[] CreatePaddedJpeg(int targetBytes)
    {
        if (targetBytes <= Jpeg1x1.Length) return Jpeg1x1.ToArray();

        // Build directly into the final byte[] so generating a large synthetic
        // attachment does not temporarily require a second List<byte> of the same size.
        var result = new byte[targetBytes];
        Buffer.BlockCopy(Jpeg1x1, 0, result, 0, 2); // SOI
        var pos = 2;
        var remaining = targetBytes - Jpeg1x1.Length;

        // Insert valid COM segments before the original image tail.
        while (remaining >= 5)
        {
            var payload = Math.Min(65533, remaining - 4);
            var segmentLength = payload + 2;
            result[pos++] = 0xFF;
            result[pos++] = 0xFE;
            result[pos++] = (byte)(segmentLength >> 8);
            result[pos++] = (byte)segmentLength;
            for (int i = 0; i < payload; i++)
                result[pos++] = (byte)((i * 17) & 0xFF);
            remaining -= payload + 4;
        }

        // A 1–4 byte remainder is harmless trailing data after EOI; keep the
        // target size exact rather than returning a shorter attachment.
        Buffer.BlockCopy(Jpeg1x1, 2, result, pos, Jpeg1x1.Length - 2);
        return result;
    }

    private static byte[] CreatePaddedPng(int targetBytes)
    {
        if (targetBytes <= Png1x1.Length) return Png1x1.ToArray();
        var keyword = Encoding.ASCII.GetBytes("MailLoadTester");
        const int chunkOverhead = 12; // length + type + CRC
        var dataLen = targetBytes - Png1x1.Length - chunkOverhead;
        // The tEXt chunk data is at minimum "keyword + \0 separator" — anything
        // smaller than that cannot be built without overrunning the target size,
        // so fall back to the minimal PNG rather than corrupt/overflow the buffer.
        if (dataLen < keyword.Length + 1) return Png1x1.ToArray();

        // Allocate the final payload once. This avoids the former chunkData +
        // List<byte> + ToArray triple-allocation for large synthetic PNGs.
        var result = new byte[targetBytes];
        var prefixLen = Png1x1.Length - 12; // keep original IEND at the end
        Buffer.BlockCopy(Png1x1, 0, result, 0, prefixLen);
        var pos = prefixLen;

        result[pos++] = (byte)(dataLen >> 24);
        result[pos++] = (byte)(dataLen >> 16);
        result[pos++] = (byte)(dataLen >> 8);
        result[pos++] = (byte)dataLen;
        result[pos++] = (byte)'t';
        result[pos++] = (byte)'E';
        result[pos++] = (byte)'X';
        result[pos++] = (byte)'t';

        Buffer.BlockCopy(keyword, 0, result, pos, keyword.Length);
        pos += keyword.Length;
        result[pos++] = 0;
        for (int i = keyword.Length + 1; i < dataLen; i++)
            result[pos++] = (byte)((i * 29) & 0xFF);

        var crc = Crc32(result, prefixLen + 4, 4 + dataLen);
        result[pos++] = (byte)(crc >> 24);
        result[pos++] = (byte)(crc >> 16);
        result[pos++] = (byte)(crc >> 8);
        result[pos++] = (byte)crc;
        Buffer.BlockCopy(Png1x1, Png1x1.Length - 12, result, pos, 12);
        return result;
    }

    private static uint Crc32(byte[] data, int offset, int count)
    {
        uint crc = 0xFFFFFFFF;
        var end = checked(offset + count);
        for (var i = offset; i < end; i++)
        {
            crc ^= data[i];
            for (int k = 0; k < 8; k++)
                crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(int)(crc & 1));
        }
        return ~crc;
    }
}
