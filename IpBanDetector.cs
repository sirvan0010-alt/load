namespace MailLoadTester;

/// <summary>
/// Heuristika odpovědí SMTP, které typicky znamenají blokaci IP/proxy (ne běžný greylist).
/// </summary>
public static class IpBanDetector
{
    private static readonly string[] Markers =
    {
        "550 5.7.1",
        "554 5.7.1",
        "blocked",
        "blacklist",
        "blacklisted",
        "spamhaus",
        "listed on",
        "rejected due to",
        "too many connections from",
        "rate limit exceeded",
        "dynamic ip",
        "suspicious activity",
        "access denied",
        "client host rejected",
        "not accepting mail from"
    };

    public static bool IsLikelyIpOrProxyBan(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return false;
        foreach (var m in Markers)
        {
            if (message.Contains(m, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
