using System.Net;
using System.Net.Sockets;

namespace MailLoadTester;

/// <summary>
/// Rotace lokálních IPv4 source adres pro nová SMTP spojení.
/// Podporuje:
/// 1) Explicitní seznam: <c>192.0.2.10,192.0.2.11,192.0.2.12</c>
/// 2) CIDR: <c>192.0.2.0/28</c> (generuje použitelné host adresy v rozsahu)
///
/// OS musí mít adresy na rozhraní (nebo povolený non-local bind), jinak Connect selže.
/// Thread-safe (round-robin přes Interlocked).
/// </summary>
public sealed class IpV4Rotator
{
    private readonly IPAddress[] _addresses;
    private int _index = -1;

    public IReadOnlyList<IPAddress> Addresses => _addresses;
    public int Count => _addresses.Length;

    public IpV4Rotator(string specification)
    {
        if (string.IsNullOrWhiteSpace(specification))
            throw new ArgumentException("IPv4 specifikace je povinná.", nameof(specification));

        var list = new List<IPAddress>();
        var parts = specification.Split(new[] { ',', ';', ' ', '\t', '\r', '\n' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        const int maxAddresses = 4096;
        foreach (var part in parts)
        {
            if (part.Contains('/'))
            {
                // ExpandCidr is a lazy yield-based generator specifically so a config
                // typo (e.g. "/8" instead of "/28") can be rejected here, mid-expansion,
                // instead of first fully materializing millions/billions of IPAddress
                // objects into `list` and only checking the size limit afterwards.
                foreach (var ip in ExpandCidr(part))
                {
                    list.Add(ip);
                    if (list.Count > maxAddresses)
                        throw new ArgumentException(
                            $"IPv4 rotace: max {maxAddresses} adres (zmenši CIDR/seznam). " +
                            $"CIDR '{part}' samo o sobě generuje výrazně víc adres.");
                }
            }
            else
            {
                if (!IPAddress.TryParse(part, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
                    throw new ArgumentException($"Neplatná IPv4 adresa: {part}");
                list.Add(ip);
            }
        }

        // Unikátní, stabilní pořadí
        _addresses = list
            .Distinct()
            .OrderBy(a => a.GetAddressBytes()[0])
            .ThenBy(a => a.GetAddressBytes()[1])
            .ThenBy(a => a.GetAddressBytes()[2])
            .ThenBy(a => a.GetAddressBytes()[3])
            .ToArray();

        if (_addresses.Length == 0)
            throw new ArgumentException("IPv4 rotace neobsahuje žádnou adresu.");
        if (_addresses.Length > maxAddresses)
            throw new ArgumentException($"IPv4 rotace: max {maxAddresses} adres (zmenši CIDR/seznam).");
    }

    public IPAddress GetNextIp()
    {
        var i = Interlocked.Increment(ref _index);
        if (i < 0)
        {
            // overflow wrap
            Interlocked.Exchange(ref _index, 0);
            i = 0;
        }
        return _addresses[i % _addresses.Length];
    }

    /// <summary>Náhodný výběr místo round-robin (volitelné).</summary>
    public IPAddress GetRandomIp() =>
        _addresses[Random.Shared.Next(_addresses.Length)];

    internal static IEnumerable<IPAddress> ExpandCidr(string cidr)
    {
        var bits = cidr.Split('/', 2);
        if (bits.Length != 2 ||
            !IPAddress.TryParse(bits[0], out var network) ||
            network.AddressFamily != AddressFamily.InterNetwork ||
            !int.TryParse(bits[1], out var prefixLen) ||
            prefixLen is < 0 or > 32)
            throw new ArgumentException($"Neplatný IPv4 CIDR: {cidr}");

        if (prefixLen > 30)
        {
            // /31 a /32 — vrať přímo síťovou adresu (žádný klasický broadcast)
            yield return network;
            yield break;
        }

        if (prefixLen < 20)
            throw new ArgumentException(
                $"IPv4 CIDR /{prefixLen} je příliš široký (minimum /20 kvůli limitu 4096 adres). Zadaný: {cidr}");

        var netBytes = network.GetAddressBytes();
        if (BitConverter.IsLittleEndian)
            Array.Reverse(netBytes);
        var netInt = BitConverter.ToUInt32(netBytes, 0);
        var mask = prefixLen == 0 ? 0u : uint.MaxValue << (32 - prefixLen);
        netInt &= mask;
        var hostCount = 1u << (32 - prefixLen);
        // přeskoč network a broadcast
        for (uint h = 1; h < hostCount - 1; h++)
        {
            var addrInt = netInt + h;
            var b = BitConverter.GetBytes(addrInt);
            if (BitConverter.IsLittleEndian)
                Array.Reverse(b);
            yield return new IPAddress(b);
        }
    }
}
