using DnsClient;

namespace MailLoadTester;

/// <summary>
/// Pre-flight kontrola DNS politik odesílací domény (SPF + DMARC).
/// Používá DnsClient.NET (nativní TXT dotazy, bez nslookup.exe).
/// DKIM se nekontroluje plošně – vyžaduje znalost selectoru.
/// </summary>
public static class DnsPolicyChecker
{
    private static readonly LookupClient Dns = new();

    public sealed record PolicyResult(
        string Domain,
        bool HasSpf,
        string? SpfRecord,
        bool HasDmarc,
        string? DmarcRecord,
        string Summary,
        bool SpfCheckFailed = false,
        bool DmarcCheckFailed = false);

    /// <summary>
    /// Extrahuje doménu z e-mailové adresy (část za @).
    /// </summary>
    public static string? ExtractDomain(string email)
    {
        if (string.IsNullOrWhiteSpace(email)) return null;
        var at = email.LastIndexOf('@');
        if (at < 0 || at >= email.Length - 1) return null;
        return email[(at + 1)..].Trim().TrimEnd('.').ToLowerInvariant();
    }

    public static async Task<bool> HasSpfRecordAsync(string domain, CancellationToken ct = default)
    {
        var r = await CheckAsync(domain, ct).ConfigureAwait(false);
        return r.HasSpf;
    }

    public static async Task<bool> HasDmarcRecordAsync(string domain, CancellationToken ct = default)
    {
        var r = await CheckAsync(domain, ct).ConfigureAwait(false);
        return r.HasDmarc;
    }

    public static async Task<PolicyResult> CheckAsync(string domain, CancellationToken ct = default)
    {
        domain = domain.Trim().TrimEnd('.').ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(domain))
            return new PolicyResult("", false, null, false, null, "Prázdná doména.");

        string? spf = null;
        string? dmarc = null;
        bool hasSpf = false;
        bool hasDmarc = false;
        bool spfCheckFailed = false;
        bool dmarcCheckFailed = false;

        try
        {
            var txt = await Dns.QueryAsync(domain, QueryType.TXT, cancellationToken: ct).ConfigureAwait(false);
            foreach (var rec in txt.Answers.TxtRecords())
            {
                var text = string.Concat(rec.Text).Trim();
                if (text.StartsWith("v=spf1", StringComparison.OrdinalIgnoreCase))
                {
                    hasSpf = true;
                    spf = text.Length > 200 ? text[..200] + "…" : text;
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // DNS chyba / timeout — "hasSpf = false" here does NOT mean "confirmed
            // no SPF record": it means the check itself couldn't complete. Track that
            // distinction separately so callers (and the GUI warning dialog) don't
            // tell the user their domain is missing SPF/DMARC when the truth is
            // simply "DNS didn't answer this time".
            spfCheckFailed = true;
        }

        try
        {
            var dmarcHost = "_dmarc." + domain;
            var txt = await Dns.QueryAsync(dmarcHost, QueryType.TXT, cancellationToken: ct).ConfigureAwait(false);
            foreach (var rec in txt.Answers.TxtRecords())
            {
                var text = string.Concat(rec.Text).Trim();
                if (text.StartsWith("v=DMARC1", StringComparison.OrdinalIgnoreCase))
                {
                    hasDmarc = true;
                    dmarc = text.Length > 200 ? text[..200] + "…" : text;
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            dmarcCheckFailed = true;
        }

        var parts = new List<string>();
        parts.Add(spfCheckFailed ? "SPF: NEOVĚŘENO (DNS chyba)" : hasSpf ? "SPF: OK" : "SPF: CHYBÍ");
        parts.Add(dmarcCheckFailed ? "DMARC: NEOVĚŘENO (DNS chyba)" : hasDmarc ? "DMARC: OK" : "DMARC: CHYBÍ");
        if (spf != null) parts.Add("SPF záznam: " + spf);
        if (dmarc != null) parts.Add("DMARC záznam: " + dmarc);

        return new PolicyResult(domain, hasSpf, spf, hasDmarc, dmarc, string.Join(" | ", parts),
            spfCheckFailed, dmarcCheckFailed);
    }
}
