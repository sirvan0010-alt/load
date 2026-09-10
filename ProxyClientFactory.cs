using System.Net;
using MailKit.Net.Proxy;

namespace MailLoadTester;

public enum ProxyType { None, Http, Socks4, Socks5 }

/// <summary>Jedna proxy položka pro rotaci.</summary>
public sealed record ProxyEndpoint(
    ProxyType Type,
    string Host,
    int Port,
    string? Username = null,
    string? Password = null)
{
    public string DisplayKey => $"{Type}:{Host}:{Port}";
}

public static class ProxyClientFactory
{
    public static IProxyClient? Create(ProxyEndpoint? ep)
    {
        if (ep is null || ep.Type == ProxyType.None || string.IsNullOrWhiteSpace(ep.Host))
            return null;

        NetworkCredential? cred = string.IsNullOrEmpty(ep.Username)
            ? null
            : new NetworkCredential(ep.Username, ep.Password ?? "");

        return ep.Type switch
        {
            ProxyType.Http => cred is null
                ? new HttpProxyClient(ep.Host, ep.Port)
                : new HttpProxyClient(ep.Host, ep.Port, cred),
            ProxyType.Socks4 => cred is null
                ? new Socks4Client(ep.Host, ep.Port)
                : new Socks4Client(ep.Host, ep.Port, cred),
            ProxyType.Socks5 => cred is null
                ? new Socks5Client(ep.Host, ep.Port)
                : new Socks5Client(ep.Host, ep.Port, cred),
            _ => null
        };
    }

    /// <summary>
    /// Parsuje seznam proxy. Formáty (řádek nebo středník):
    /// socks5://host:1080
    /// http://user:pass@host:8080
    /// host:1080 (default SOCKS5)
    /// socks5:host:1080:user:pass
    /// </summary>
    public static List<ProxyEndpoint> ParseList(string? text)
    {
        var result = new List<ProxyEndpoint>();
        if (string.IsNullOrWhiteSpace(text)) return result;

        var parts = text.Split(new[] { '\r', '\n', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var raw in parts)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            if (TryParseUri(line, out var ep) || TryParseColon(line, out ep))
                result.Add(ep!);
            else
                throw new ArgumentException($"Neplatná proxy položka: {line}");
        }
        return result;
    }

    static bool TryParseUri(string line, out ProxyEndpoint? ep)
    {
        ep = null;
        if (!line.Contains("://", StringComparison.Ordinal)) return false;
        if (!Uri.TryCreate(line, UriKind.Absolute, out var uri)) return false;

        var type = uri.Scheme.ToLowerInvariant() switch
        {
            "http" or "https" => ProxyType.Http,
            "socks4" => ProxyType.Socks4,
            "socks5" or "socks" => ProxyType.Socks5,
            _ => ProxyType.None
        };
        if (type == ProxyType.None) return false;

        var user = uri.UserInfo;
        string? userName = null, password = null;
        if (!string.IsNullOrEmpty(user))
        {
            var up = user.Split(':', 2);
            userName = Uri.UnescapeDataString(up[0]);
            if (up.Length > 1) password = Uri.UnescapeDataString(up[1]);
        }

        var port = uri.Port > 0 ? uri.Port : (type == ProxyType.Http ? 8080 : 1080);
        ep = new ProxyEndpoint(type, uri.Host, port, userName, password);
        return true;
    }

    static bool TryParseColon(string line, out ProxyEndpoint? ep)
    {
        ep = null;
        var bits = line.Split(':');
        // host:port
        if (bits.Length == 2 && int.TryParse(bits[1], out var port2))
        {
            ep = new ProxyEndpoint(ProxyType.Socks5, bits[0], port2);
            return true;
        }
        // type:host:port
        if (bits.Length == 3 && Enum.TryParse<ProxyType>(bits[0], true, out var t3) && int.TryParse(bits[2], out var port3))
        {
            ep = new ProxyEndpoint(t3, bits[1], port3);
            return true;
        }
        // type:host:port:user:pass
        if (bits.Length >= 5 && Enum.TryParse<ProxyType>(bits[0], true, out var t5) && int.TryParse(bits[2], out var port5))
        {
            ep = new ProxyEndpoint(t5, bits[1], port5, bits[3], string.Join(':', bits.Skip(4)));
            return true;
        }
        return false;
    }
}
