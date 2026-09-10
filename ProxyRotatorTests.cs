using Xunit;

namespace MailLoadTester.Tests;

public sealed class ProxyRotatorTests
{
    [Fact]
    public void Parses_Uri_And_RoundRobin()
    {
        var list = ProxyClientFactory.ParseList("socks5://127.0.0.1:1080\nhttp://10.0.0.1:8080");
        Assert.Equal(2, list.Count);
        Assert.Equal(ProxyType.Socks5, list[0].Type);
        Assert.Equal(ProxyType.Http, list[1].Type);

        var rot = new ProxyRotator(list);
        var a = rot.TryGetNext();
        var b = rot.TryGetNext();
        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.NotEqual(a!.DisplayKey, b!.DisplayKey);
    }

    [Fact]
    public void Ban_Skips_Proxy()
    {
        var list = ProxyClientFactory.ParseList("socks5://127.0.0.1:1080;socks5://127.0.0.1:1081");
        var rot = new ProxyRotator(list, blockDuration: TimeSpan.FromMinutes(10));
        var first = rot.TryGetNext()!;
        rot.ReportBlocked(first);
        for (int i = 0; i < 5; i++)
            Assert.NotEqual(first.DisplayKey, rot.TryGetNext()!.DisplayKey);
    }

    [Fact]
    public void BanDetector_Matches_550()
    {
        Assert.True(IpBanDetector.IsLikelyIpOrProxyBan("550 5.7.1 Blocked"));
        Assert.False(IpBanDetector.IsLikelyIpOrProxyBan("450 Try again later"));
    }
}
