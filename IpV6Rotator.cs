using System.Net;
using System.Net.Sockets;

namespace MailLoadTester;

/// <summary>
/// Generuje náhodné IPv6 adresy uvnitř zadaného síťového prefixu (výchozí /64).
/// Pro load test s více source adresami — OS musí mít prefix routovaný na rozhraní
/// a právo bindovat zvolené adresy (jinak Connect selže a pool spojení zahodí).
/// </summary>
public sealed class IpV6Rotator
{
    private readonly byte[] _prefix;
    private readonly int _prefixByteCount;

    /// <param name="networkPrefix">Např. <c>2001:db8:85a3:0::</c> (prvních 64 bitů = síť).</param>
    /// <param name="prefixLength">Délka prefixu v bitech (1–128). Typicky 64.</param>
    public IpV6Rotator(string networkPrefix, int prefixLength = 64)
    {
        if (string.IsNullOrWhiteSpace(networkPrefix))
            throw new ArgumentException("IPv6 prefix je povinný.", nameof(networkPrefix));

        var ip = IPAddress.Parse(networkPrefix.Trim());
        if (ip.AddressFamily != AddressFamily.InterNetworkV6)
            throw new ArgumentException("Očekávána IPv6 adresa.", nameof(networkPrefix));

        prefixLength = Math.Clamp(prefixLength, 1, 128);
        _prefixByteCount = prefixLength / 8;
        var prefixBitsRem = prefixLength % 8;

        var bytes = ip.GetAddressBytes();
        _prefix = new byte[16];
        Array.Copy(bytes, 0, _prefix, 0, _prefixByteCount);
        if (prefixBitsRem > 0 && _prefixByteCount < 16)
        {
            // Zachovej jen horní bity částečného bytu.
            var mask = (byte)(0xFF << (8 - prefixBitsRem));
            _prefix[_prefixByteCount] = (byte)(bytes[_prefixByteCount] & mask);
        }
        // Ulož prefix length pro generování
        PrefixLength = prefixLength;
    }

    public int PrefixLength { get; }

    /// <summary>Vrátí novou náhodnou adresu v rozsahu prefixu.</summary>
    public IPAddress GetNextRandomIp()
    {
        var addr = new byte[16];
        Array.Copy(_prefix, 0, addr, 0, 16);

        var hostBits = 128 - PrefixLength;
        if (hostBits <= 0)
            return new IPAddress(addr);

        var random = new byte[(hostBits + 7) / 8];
        Random.Shared.NextBytes(random);

        // Zapiš host část od konce
        var bitOffset = PrefixLength;
        for (int i = 0; i < hostBits; i++)
        {
            var byteIndex = (bitOffset + i) / 8;
            var bitInByte = 7 - ((bitOffset + i) % 8);
            var rndByte = random[i / 8];
            var rndBit = (rndByte >> (7 - (i % 8))) & 1;
            if (rndBit == 1)
                addr[byteIndex] |= (byte)(1 << bitInByte);
            else
                addr[byteIndex] &= (byte)~(1 << bitInByte);
        }

        return new IPAddress(addr);
    }
}
