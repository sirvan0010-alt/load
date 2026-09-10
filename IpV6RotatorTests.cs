using System.Net;
using System.Net.Sockets;
using Xunit;

namespace MailLoadTester.Tests;

public sealed class IpV6RotatorTests
{
    [Fact]
    public void Generates_Addresses_In_Prefix()
    {
        var rot = new IpV6Rotator("2001:db8:85a3:0::", 64);
        for (int i = 0; i < 20; i++)
        {
            var ip = rot.GetNextRandomIp();
            Assert.Equal(AddressFamily.InterNetworkV6, ip.AddressFamily);
            var b = ip.GetAddressBytes();
            // first 8 bytes = 20 01 0d b8 85 a3 00 00
            Assert.Equal(0x20, b[0]);
            Assert.Equal(0x01, b[1]);
            Assert.Equal(0x0d, b[2]);
            Assert.Equal(0xb8, b[3]);
            Assert.Equal(0x85, b[4]);
            Assert.Equal(0xa3, b[5]);
            Assert.Equal(0x00, b[6]);
            Assert.Equal(0x00, b[7]);
        }
    }

    [Fact]
    public void Rejects_IPv4()
    {
        Assert.ThrowsAny<Exception>(() => new IpV6Rotator("192.0.2.1", 64));
    }
}
