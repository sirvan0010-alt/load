using System.Net;
using System.Net.Sockets;

namespace MailLoadTester;

public static class IpBindingHelper
{
    public static IPEndPoint? ResolveLocalEndPoint(string? sourceIp, IpVersionPreference preference)
    {
        if (string.IsNullOrWhiteSpace(sourceIp))
        {
            return preference switch
            {
                IpVersionPreference.IPv4Only => new IPEndPoint(IPAddress.Any, 0),
                IpVersionPreference.IPv6Only => new IPEndPoint(IPAddress.IPv6Any, 0),
                _ => null
            };
        }
        if (IPAddress.TryParse(sourceIp, out var ip))
        {
            if (preference == IpVersionPreference.IPv4Only && ip.AddressFamily != AddressFamily.InterNetwork)
                throw new ArgumentException("Source IP není IPv4 adresa.");
            if (preference == IpVersionPreference.IPv6Only && ip.AddressFamily != AddressFamily.InterNetworkV6)
                throw new ArgumentException("Source IP není IPv6 adresa.");
            return new IPEndPoint(ip, 0);
        }
        throw new ArgumentException("Neplatná Source IP adresa.");
    }

    public static Socket CreateBoundSocket(IPEndPoint? localEp, IpVersionPreference preference)
    {
        // If we have a concrete local endpoint, the socket's address family MUST
        // match it — Socket.Bind() does not auto-convert a plain IPv4 IPEndPoint
        // onto a dual-mode IPv6 socket (that only works with IPv4-mapped
        // addresses like ::ffff:x.x.x.x, which we don't construct here). The
        // previous version always created an IPv6 dual-mode socket whenever
        // preference was Any/DualStack, so binding a plain IPv4 SourceIp under
        // the *default* IpVersion=Any setting threw SocketException on every
        // single connection attempt.
        AddressFamily af;
        bool enableDualMode;

        if (localEp != null)
        {
            af = localEp.AddressFamily;
            enableDualMode = af == AddressFamily.InterNetworkV6 &&
                (preference == IpVersionPreference.Any || preference == IpVersionPreference.DualStack);
        }
        else
        {
            af = preference switch
            {
                IpVersionPreference.IPv4Only => AddressFamily.InterNetwork,
                IpVersionPreference.IPv6Only => AddressFamily.InterNetworkV6,
                _ => AddressFamily.InterNetworkV6
            };
            enableDualMode = preference == IpVersionPreference.Any || preference == IpVersionPreference.DualStack;
        }

        var socket = new Socket(af, SocketType.Stream, ProtocolType.Tcp);
        if (enableDualMode)
        {
            socket.DualMode = true;
        }
        if (localEp != null)
        {
            socket.Bind(localEp);
        }
        return socket;
    }
}
