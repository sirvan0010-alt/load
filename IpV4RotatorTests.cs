using System.Net;
using System.Net.Sockets;
using Xunit;

namespace MailLoadTester.Tests;

public sealed class IpV4RotatorTests
{
    [Fact]
    public void Parses_List_And_RoundRobin()
    {
        var rot = new IpV4Rotator("192.0.2.10, 192.0.2.11");
        Assert.Equal(2, rot.Count);
        var a = rot.GetNextIp();
        var b = rot.GetNextIp();
        var c = rot.GetNextIp();
        Assert.Equal(AddressFamily.InterNetwork, a.AddressFamily);
        Assert.NotEqual(a, b);
        Assert.Equal(a, c); // round-robin
    }

    [Fact]
    public void Expands_Cidr_Slash28()
    {
        // /28 = 16 addresses, 14 usable hosts
        var rot = new IpV4Rotator("192.0.2.0/28");
        Assert.Equal(14, rot.Count);
        Assert.All(rot.Addresses, ip => Assert.Equal(AddressFamily.InterNetwork, ip.AddressFamily));
        Assert.DoesNotContain(IPAddress.Parse("192.0.2.0"), rot.Addresses);
        Assert.DoesNotContain(IPAddress.Parse("192.0.2.15"), rot.Addresses);
    }

    [Fact]
    public void Rejects_Empty_And_Bad()
    {
        Assert.ThrowsAny<Exception>(() => new IpV4Rotator(""));
        Assert.ThrowsAny<Exception>(() => new IpV4Rotator("not-an-ip"));
        Assert.ThrowsAny<Exception>(() => new IpV4Rotator("2001:db8::1"));
    }

    [Fact]
    public void OversizedCidr_FailsFastWithoutMaterializingMillionsOfAddresses()
    {
        // Regression test: the >4096 guard used to run only *after* fully expanding
        // the CIDR into a List<IPAddress> — a typo like "/8" instead of "/28" (or
        // worse, "/1") would try to allocate millions/billions of IPAddress objects
        // before the limit was ever checked. Must now fail quickly and cheaply.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ex = Assert.ThrowsAny<Exception>(() => new IpV4Rotator("10.0.0.0/8"));
        sw.Stop();

        Assert.Contains("4096", ex.Message);
        Assert.True(sw.ElapsedMilliseconds < 5000,
            $"Oversized CIDR should fail fast, took {sw.ElapsedMilliseconds}ms");
    }
}
