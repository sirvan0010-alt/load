using System.Net;
using System.Net.Sockets;
using Xunit;

namespace MailLoadTester.Tests;

public sealed class IpBindingHelperTests
{
    [Theory]
    [InlineData(IpVersionPreference.Any)]
    [InlineData(IpVersionPreference.DualStack)]
    [InlineData(IpVersionPreference.IPv4Only)]
    public void CreateBoundSocket_WithIPv4SourceIp_BindsSuccessfully(IpVersionPreference preference)
    {
        // Regression test: CreateBoundSocket used to always create an
        // AddressFamily.InterNetworkV6 dual-mode socket whenever preference was
        // Any/DualStack, then try to Bind() a plain IPv4 IPEndPoint onto it —
        // which .NET rejects with SocketException (address family mismatch).
        // That broke source-IP binding under the *default* IpVersion=Any setting
        // for any plain IPv4 SourceIp.
        var localEp = IpBindingHelper.ResolveLocalEndPoint("127.0.0.1", preference);
        Assert.NotNull(localEp);

        using var socket = IpBindingHelper.CreateBoundSocket(localEp, preference);

        Assert.Equal(AddressFamily.InterNetwork, socket.AddressFamily);
        Assert.Equal(AddressFamily.InterNetwork, ((IPEndPoint)socket.LocalEndPoint!).AddressFamily);
    }

    [Theory]
    [InlineData(IpVersionPreference.Any)]
    [InlineData(IpVersionPreference.DualStack)]
    [InlineData(IpVersionPreference.IPv6Only)]
    public void CreateBoundSocket_WithIPv6SourceIp_BindsSuccessfully(IpVersionPreference preference)
    {
        var localEp = IpBindingHelper.ResolveLocalEndPoint("::1", preference);
        Assert.NotNull(localEp);

        using var socket = IpBindingHelper.CreateBoundSocket(localEp, preference);

        Assert.Equal(AddressFamily.InterNetworkV6, socket.AddressFamily);
    }

    [Fact]
    public void CreateBoundSocket_NoSourceIp_DefaultsToDualModeV6()
    {
        using var socket = IpBindingHelper.CreateBoundSocket(null, IpVersionPreference.Any);
        Assert.Equal(AddressFamily.InterNetworkV6, socket.AddressFamily);
        Assert.True(socket.DualMode);
    }
}
