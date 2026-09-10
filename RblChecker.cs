using System.Net;
using System.Net.Sockets;

namespace MailLoadTester;

/// <summary>
/// Jednoduchá DNSBL (RBL) kontrola source IP.
/// Výchozí: Spamhaus Zen. Pouze informační – nikdy neblokuje start automaticky.
/// </summary>
public static class RblChecker
{
    public sealed record RblResult(
        string Ip,
        bool IsListed,
        string Zone,
        string Detail,
        string? LookupHost,
        bool IsUnknown = false);

    /// <summary>
    /// Zkontroluje IP proti Spamhaus Zen.
    /// Pro IPv4: reversed IP + .zen.spamhaus.org
    /// </summary>
    public static async Task<RblResult> CheckSpamhausZenAsync(string ip, CancellationToken ct = default)
    {
        if (!IPAddress.TryParse(ip, out var addr) || addr.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return new RblResult(ip, false, "zen.spamhaus.org",
                "RBL kontrola podporuje pouze IPv4 adresu.", null);
        }

        var octets = ip.Split('.');
        if (octets.Length != 4)
            return new RblResult(ip, false, "zen.spamhaus.org", "Neplatná IPv4 adresa.", null);

        var reversed = $"{octets[3]}.{octets[2]}.{octets[1]}.{octets[0]}.zen.spamhaus.org";
        try
        {
            ct.ThrowIfCancellationRequested();
            var hosts = await Dns.GetHostAddressesAsync(reversed, ct).ConfigureAwait(false);
            if (hosts.Length > 0)
            {
                return new RblResult(ip, true, "zen.spamhaus.org",
                    $"IP je na blacklistu Spamhaus Zen. Návratové adresy: {string.Join(", ", hosts.Select(h => h.ToString()))}. " +
                    "Doporučení: ověřit https://www.spamhaus.org/lookup/ a řešit delist.",
                    reversed);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (SocketException ex)
        {
            // SocketException is thrown for BOTH a genuine NXDOMAIN (HostNotFound/
            // NoData — meaning the DNS query succeeded and the IP truly is not
            // listed) AND for real query failures (timeout, unreachable resolver,
            // TryAgain). Treating every SocketException as "not listed" reported a
            // false-clean result whenever the DNS query itself simply failed to
            // complete — SocketErrorCode lets us tell these apart.
            if (ex.SocketErrorCode is SocketError.HostNotFound or SocketError.NoData)
            {
                return new RblResult(ip, false, "zen.spamhaus.org",
                    "IP nebyla nalezena na Spamhaus Zen (NXDOMAIN — čistá).", reversed);
            }
            return new RblResult(ip, false, "zen.spamhaus.org",
                $"RBL dotaz selhal ({ex.SocketErrorCode}) — výsledek NEOVĚŘEN, nejde o potvrzeně čistou IP. Zkuste znovu.",
                reversed, IsUnknown: true);
        }
        catch (ArgumentException)
        {
            return new RblResult(ip, false, "zen.spamhaus.org",
                "IP nebyla nalezena na Spamhaus Zen (NXDOMAIN — čistá).", reversed);
        }
        catch
        {
            // Síťová chyba – nehlasit jako listed
            return new RblResult(ip, false, "zen.spamhaus.org",
                "RBL dotaz selhal (síťová chyba) — výsledek NEOVĚŘEN. Zkuste znovu nebo zkontrolujte ručně.",
                reversed, IsUnknown: true);
        }

        return new RblResult(ip, false, "zen.spamhaus.org",
            "IP nebyla nalezena na Spamhaus Zen (čistá nebo NXDOMAIN).", reversed);
    }
}
