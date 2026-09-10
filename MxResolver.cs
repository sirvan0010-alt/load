using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace MailLoadTester;

/// <summary>
/// Resolves real MX records via a raw DNS query (UDP, type 15 = MX).
/// Validates transaction ID, QR flag, RCODE, QTYPE/QCLASS and compression pointers.
/// Falls back to A/AAAA only if the domain has no MX (RFC 5321 §5.1).
/// </summary>
public static class MxResolver
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTimeOffset Expires, IReadOnlyList<MxRecord> Records)> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);
    // A DNS query that outright failed (timeout, unreachable resolver, spoofed/wrong
    // source packet, malformed response) is NOT the same fact as "this domain has no
    // MX/A record". Caching both the same way used to mean one transient DNS blip
    // mid-run poisoned the cache for the full 10 minutes, failing every subsequent
    // message to an otherwise-healthy domain. Failed lookups get a much shorter TTL
    // so the resolver retries soon instead of trusting a single bad sample.
    private static readonly TimeSpan FailedLookupCacheTtl = TimeSpan.FromSeconds(15);
    private const int MaxCacheEntries = 512;

    public static async Task<IReadOnlyList<MxRecord>> ResolveAsync(string domain, CancellationToken ct, int timeoutMs = 4000)
    {
        domain = domain.Trim().TrimEnd('.').ToLowerInvariant();
        if (string.IsNullOrEmpty(domain))
            return Array.Empty<MxRecord>();

        if (Cache.TryGetValue(domain, out var hit) && hit.Expires > DateTimeOffset.UtcNow)
            return hit.Records;

        var raw = await QueryMxAsync(domain, ct, timeoutMs).ConfigureAwait(false);
        IReadOnlyList<MxRecord> result;
        var confirmed = raw != null; // null = query failed/timed out, not a confirmed "no MX"
        if (raw is { Count: > 0 })
            result = raw.OrderBy(r => r.Preference).ToList();
        else
        {
            try
            {
                var entry = await Dns.GetHostEntryAsync(domain, ct).ConfigureAwait(false);
                result = entry.AddressList.Length > 0
                    ? new[] { new MxRecord(domain, 0, entry.AddressList[0]) }
                    : Array.Empty<MxRecord>();
                confirmed = true; // GetHostEntryAsync succeeded — a genuine, trustworthy answer
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                result = Array.Empty<MxRecord>();
                confirmed = false; // A/AAAA lookup itself failed — still not a confirmed negative
            }
        }

        var ttl = confirmed ? CacheTtl : FailedLookupCacheTtl;
        Cache[domain] = (DateTimeOffset.UtcNow.Add(ttl), result);
        PruneCacheIfNeeded();
        return result;
    }

    public static string GetBestHost(IReadOnlyList<MxRecord> records) =>
        records.FirstOrDefault()?.Host ?? throw new InvalidOperationException("Doména nemá žádný MX ani A záznam.");

    static async Task<List<MxRecord>?> QueryMxAsync(string domain, CancellationToken ct, int timeoutMs)
    {
        var results = new List<MxRecord>();
        var server = GetDnsServer();
        var (query, txId) = BuildQuery(domain);

        using var udp = new UdpClient();
        try
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linkedCts.CancelAfter(timeoutMs);

            await udp.SendAsync(query, query.Length, new IPEndPoint(server, 53)).ConfigureAwait(false);

            var receiveTask = udp.ReceiveAsync(linkedCts.Token).AsTask();
            var received = await receiveTask.ConfigureAwait(false);
            // UDP DNS is unauthenticated. At minimum, only accept a packet from
            // the resolver we actually queried; transaction-ID checking alone is
            // not sufficient because an attacker could race/spoof the response.
            // This is an untrustworthy/failed lookup, not a confirmed "no MX".
            if (received.RemoteEndPoint.Address != server || received.RemoteEndPoint.Port != 53)
                return null;

            var buffer = received.Buffer;

            // A response we can't parse is not a confirmed negative either — it
            // could be a corrupted/truncated/unexpected packet, not proof the
            // domain has no MX records.
            if (!TryParseMxResponse(buffer, txId, out var names, out var prefs))
                return null;

            for (int i = 0; i < names.Count; i++)
                results.Add(new MxRecord(names[i], prefs[i], IPAddress.None));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null; // Timeout / send failure / socket error — not a confirmed negative.
        }

        return results;
    }

    static IPAddress GetDnsServer()
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                foreach (var dns in nic.GetIPProperties().DnsAddresses)
                    if (dns.AddressFamily == AddressFamily.InterNetwork)
                        return dns;
            }
        }
        catch { }
        return IPAddress.Parse("1.1.1.1");
    }

    internal static (byte[] Query, ushort TransactionId) BuildQuery(string name)
    {
        var id = (ushort)Random.Shared.Next(0, ushort.MaxValue + 1);
        var labels = name.TrimEnd('.').Split('.', StringSplitOptions.RemoveEmptyEntries);

        using var ms = new MemoryStream();
        void W16(int v) { ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)v); }

        W16(id);
        W16(0x0100);
        W16(1); W16(0); W16(0); W16(0);

        foreach (var label in labels)
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            if (bytes.Length > 63) throw new ArgumentException("DNS label too long.");
            ms.WriteByte((byte)bytes.Length);
            ms.Write(bytes, 0, bytes.Length);
        }
        ms.WriteByte(0);
        W16(15);
        W16(1);
        return (ms.ToArray(), id);
    }

    internal static bool TryParseMxResponse(
        byte[] data,
        ushort expectedId,
        out List<string> names,
        out List<int> prefs)
    {
        names = new List<string>();
        prefs = new List<int>();

        if (data is null || data.Length < 12)
            return false;

        ushort id = (ushort)((data[0] << 8) | data[1]);
        if (id != expectedId)
            return false;

        int flags = (data[2] << 8) | data[3];
        if ((flags & 0x8000) == 0) // QR must indicate a response
            return false;
        if ((flags & 0x7800) != 0) // only standard QUERY opcode (0)
            return false;
        if ((flags & 0x0200) != 0) // truncated UDP response: do not parse partial data
            return false;

        int rcode = flags & 0xF;
        if (rcode is not (0 or 3))
            return false;

        int qdcount = (data[4] << 8) | data[5];
        int ancount = (data[6] << 8) | data[7];
        // This resolver sends exactly one MX/IN question. Accepting zero or
        // multiple questions makes the response ambiguous and weakens validation.
        if (qdcount != 1 || ancount < 0 || ancount > 256)
            return false;

        int pos = 12;

        for (int i = 0; i < qdcount; i++)
        {
            if (!TrySkipName(data, ref pos))
                return false;
            if (pos + 4 > data.Length)
                return false;
            int qtype = (data[pos] << 8) | data[pos + 1];
            int qclass = (data[pos + 2] << 8) | data[pos + 3];
            pos += 4;
            if (i == 0 && (qtype != 15 || qclass != 1))
                return false;
        }

        if (rcode == 3)
            return true;

        for (int i = 0; i < ancount; i++)
        {
            if (!TrySkipName(data, ref pos))
                return false;
            if (pos + 10 > data.Length)
                return false;

            int type = (data[pos] << 8) | data[pos + 1];
            int rrClass = (data[pos + 2] << 8) | data[pos + 3];
            pos += 8;
            int rdlength = (data[pos] << 8) | data[pos + 1];
            pos += 2;
            if (rdlength < 0 || pos + rdlength > data.Length)
                return false;

            if (type == 15 && rrClass == 1 && rdlength >= 3)
            {
                int preference = (data[pos] << 8) | data[pos + 1];
                if (!TryReadName(data, pos + 2, out var exchange, out var nameEnd)
                    || nameEnd > pos + rdlength
                    || string.IsNullOrWhiteSpace(exchange))
                    return false;

                names.Add(exchange.TrimEnd('.').ToLowerInvariant());
                prefs.Add(preference);
            }
            pos += rdlength;
        }

        return true;
    }

    static bool TrySkipName(byte[] data, ref int pos)
    {
        int safety = 0;
        while (pos < data.Length && safety++ < 128)
        {
            int len = data[pos];
            if (len == 0)
            {
                pos++;
                return true;
            }
            if ((len & 0xC0) == 0xC0)
            {
                if (pos + 1 >= data.Length) return false;
                int pointer = ((len & 0x3F) << 8) | data[pos + 1];
                if (pointer < 0 || pointer >= pos) return false;
                pos += 2;
                return true;
            }
            if ((len & 0xC0) != 0)
                return false;
            pos++;
            if (len > 63 || pos + len > data.Length) return false;
            pos += len;
        }
        return false;
    }

    internal static bool TryReadName(byte[] data, int pos, out string name, out int nextPos)
    {
        name = "";
        nextPos = pos;
        var labels = new List<string>();
        int endPos = pos;
        bool jumped = false;
        int jumps = 0;
        var seen = new HashSet<int>();
        bool terminated = false;

        while (pos < data.Length && jumps < 32)
        {
            if (!seen.Add(pos))
                return false;

            int len = data[pos];
            if (len == 0)
            {
                if (!jumped) endPos = pos + 1;
                terminated = true;
                break;
            }

            if ((len & 0xC0) == 0xC0)
            {
                if (pos + 1 >= data.Length) return false;
                int pointer = ((len & 0x3F) << 8) | data[pos + 1];

                // DNS compression pointers reference an earlier name. Reject
                // forward pointers because they are malformed and can make
                // validation depend on data that has not been parsed yet.
                if (pointer < 0 || pointer >= pos)
                    return false;

                if (!jumped) endPos = pos + 2;
                jumped = true;
                jumps++;
                pos = pointer;
                continue;
            }

            if ((len & 0xC0) != 0) return false;
            pos++;
            if (len > 63 || pos + len > data.Length) return false;
            labels.Add(Encoding.ASCII.GetString(data, pos, len));
            pos += len;
            if (!jumped) endPos = pos;
        }

        if (!terminated)
            return false;

        name = string.Join('.', labels);
        nextPos = endPos;
        return true;
    }

    static void PruneCacheIfNeeded()
    {
        if (Cache.Count <= MaxCacheEntries) return;
        var now = DateTimeOffset.UtcNow;
        foreach (var kv in Cache)
        {
            if (kv.Value.Expires <= now)
                Cache.TryRemove(kv.Key, out _);
        }
        if (Cache.Count > MaxCacheEntries)
        {
            foreach (var key in Cache.Keys.Take(Math.Max(0, Cache.Count - MaxCacheEntries + 32)).ToArray())
                Cache.TryRemove(key, out _);
        }
    }
}

public sealed record MxRecord(string Host, int Preference, IPAddress Address);
