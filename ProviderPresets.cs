namespace MailLoadTester;

/// <summary>
/// Předvolby poskytovatelů – vyplní doporučené hodnoty tempa a ochrany.
/// GUI: ComboBox „Předvolba poskytovatele“ → aplikuje sadu hodnot do záložky Tempo a ochrana.
/// </summary>
public static class ProviderPresets
{
    public sealed record Preset(
        string Id,
        string DisplayName,
        string Description,
        // Tempo
        int IntervalMs,
        bool EnableJitter,
        int JitterPercent,
        bool EnableBurstMode,
        int BurstSize,
        int BurstPauseSeconds,
        bool EnableProgressiveBackoff,
        int BackoffAfterSuccesses,
        double BackoffMultiplier,
        int MaxIntervalMs,
        int MaxConcurrency,
        // Ochrana
        bool DetectGreylist,
        int GreylistRetryMinutes,
        bool EnableWarmup,
        string WarmupPhases,
        // SMTP tip
        int SuggestedPort,
        SmtpSecurity SuggestedSecurity);

    public static IReadOnlyList<Preset> All { get; } =
    [
        new("Custom", "Vlastní (ruční)",
            "Všechna pole zůstávají pod vaší kontrolou.",
            1000, true, 15, false, 10, 30, true, 50, 1.5, 60_000, 5,
            true, 10, false, "50;150;500", 587, SmtpSecurity.StartTls),

        new("Gentle", "Šetrný (obecný)",
            "Delší pauzy, jitter, burst+pause, greylist. Vhodné pro neznámé servery a novou IP.",
            3000, true, 25, true, 5, 45, true, 20, 1.8, 120_000, 2,
            true, 12, true, "20;50;100", 587, SmtpSecurity.StartTls),

        new("Standard", "Standardní",
            "Vyvážený kompromis mezi rychlostí a bezpečností.",
            1000, true, 15, false, 10, 30, true, 50, 1.5, 60_000, 5,
            true, 10, false, "50;150;500", 587, SmtpSecurity.StartTls),

        new("Aggressive", "Agresivní (pouze interní)",
            "Minimální pauzy. Používejte výhradně na interních / testovacích serverech, které filtr neřeší.",
            100, false, 0, false, 50, 5, false, 1000, 1.1, 5_000, 15,
            false, 5, false, "500;2000", 25, SmtpSecurity.None),

        new("Gmail", "Gmail / Google Workspace",
            "Konzervativní tempo, warm-up, greylist. Gmail je citlivý na reputaci a objem.",
            5000, true, 20, true, 5, 60, true, 15, 2.0, 180_000, 2,
            true, 15, true, "10;30;80;200", 587, SmtpSecurity.StartTls),

        new("Microsoft365", "Microsoft 365 / Outlook",
            "Střední tempo, důraz na backoff při 4xx, greylist.",
            2500, true, 20, true, 8, 40, true, 25, 1.7, 120_000, 3,
            true, 12, true, "20;60;150", 587, SmtpSecurity.StartTls),

        new("Seznam", "Seznam.cz / Email.cz",
            "Nižší objem, delší pauzy, warm-up pro novou IP.",
            4000, true, 25, true, 5, 50, true, 15, 1.8, 150_000, 2,
            true, 10, true, "15;40;100", 587, SmtpSecurity.StartTls),

        new("SharedHosting", "Shared hosting",
            "Typické limity 100–300/hod. Burst+pause a nízký paralelismus.",
            2000, true, 20, true, 10, 60, true, 30, 1.6, 90_000, 2,
            true, 10, false, "30;80", 587, SmtpSecurity.StartTls),

        new("Internal", "Interní / firemní server",
            "Vyšší paralelismus, kratší intervaly. Stále s mírným jitterem.",
            200, true, 10, false, 50, 10, true, 100, 1.3, 30_000, 10,
            true, 8, false, "100;500", 25, SmtpSecurity.None)
    ];

    public static Preset? Find(string id)
        => All.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
}
