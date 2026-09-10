using Xunit;

namespace MailLoadTester.Tests;

public sealed class MxResolverTests
{
    [Fact]
    public void Rejects_WrongTransactionId()
    {
        var (query, id) = MxResolver.BuildQuery("example.com");
        // Forge response with different ID
        var resp = (byte[])query.Clone();
        resp[0] ^= 0xFF;
        resp[2] = 0x81; // QR=1
        resp[3] = 0x80;
        Assert.False(MxResolver.TryParseMxResponse(resp, id, out _, out _));
    }

    [Fact]
    public void Rejects_QueryFlag_NotResponse()
    {
        var (query, id) = MxResolver.BuildQuery("example.com");
        // QR bit still 0
        Assert.False(MxResolver.TryParseMxResponse(query, id, out _, out _));
    }

    [Fact]
    public void Accepts_NxDomain()
    {
        var (query, id) = MxResolver.BuildQuery("nosuch.example");
        var resp = (byte[])query.Clone();
        resp[2] = 0x81; // QR=1, RD
        resp[3] = 0x83; // RA + RCODE=3 NXDOMAIN
        Assert.True(MxResolver.TryParseMxResponse(resp, id, out var names, out _));
        Assert.Empty(names);
    }

    [Fact]
    public void BuildQuery_ContainsMxType()
    {
        var (query, _) = MxResolver.BuildQuery("mail.example.com");
        Assert.True(query.Length > 12);
        // last 4 bytes of question: type 15, class 1
        Assert.Equal(15, (query[^4] << 8) | query[^3]);
        Assert.Equal(1, (query[^2] << 8) | query[^1]);
    }

    [Fact]
    public void Rejects_TruncatedResponse()
    {
        var (query, id) = MxResolver.BuildQuery("example.com");
        var resp = (byte[])query.Clone();
        resp[2] = 0x81; // QR
        resp[3] = 0x80; // NOERROR
        Assert.False(MxResolver.TryParseMxResponse(resp[..Math.Max(12, resp.Length - 2)], id, out _, out _));
    }

    [Fact]
    public void Rejects_TruncatedFlag()
    {
        var (query, id) = MxResolver.BuildQuery("example.com");
        var resp = (byte[])query.Clone();
        resp[2] = 0x83; // QR + TC
        resp[3] = 0x80;
        Assert.False(MxResolver.TryParseMxResponse(resp, id, out _, out _));
    }

    [Fact]
    public void Rejects_CompressionPointerLoop()
    {
        // C0 00 points to itself; parser must fail closed.
        var data = new byte[] { 0xC0, 0x00 };
        Assert.False(MxResolver.TryReadName(data, 0, out _, out _));
    }

    [Fact]
    public void Rejects_ForwardCompressionPointer()
    {
        // Pointer to a later byte is malformed DNS compression.
        var data = new byte[] { 0xC0, 0x02, 0x00 };
        Assert.False(MxResolver.TryReadName(data, 0, out _, out _));
    }

    [Fact]
    public void Rejects_CompressionChainThatNeverTerminates()
    {
        // A long backward pointer chain must not be accepted merely because
        // the 32-jump safety limit was reached.
        var data = new byte[96];
        for (int pos = 32; pos < 96; pos += 2)
        {
            data[pos] = 0xC0;
            data[pos + 1] = (byte)Math.Max(0, pos - 2);
        }
        data[30] = 0xC0;
        data[31] = 0x1E; // eventually forms a loop
        Assert.False(MxResolver.TryReadName(data, 94, out _, out _));
    }

}
