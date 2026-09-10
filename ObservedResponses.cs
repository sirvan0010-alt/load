using System.Collections.Concurrent;

namespace MailLoadTester;

/// <summary>
/// Klasifikace pozorované SMTP odpovědi.
/// </summary>
public enum ResponseClassification
{
    Success,
    Greylist,
    RateLimit,
    TransientOther,
    Permanent,
    Timeout,
    Other
}

/// <summary>
/// Jeden agregovaný řádek živé tabulky pozorovaných reakcí.
/// </summary>
public sealed class ObservedResponseRow
{
    public int SmtpCode { get; init; }
    public string SampleText { get; set; } = "";
    public int Count { get; set; }
    public DateTimeOffset FirstSeen { get; set; }
    public DateTimeOffset LastSeen { get; set; }
    public ResponseClassification Classification { get; set; }
    public string RecommendedAction { get; set; } = "";
}

/// <summary>
/// Thread-safe sběrač SMTP odpovědí během testu.
/// </summary>
public sealed class ObservedResponseCollector
{
    private readonly ConcurrentDictionary<int, ObservedResponseRow> _rows = new();

    public void Record(int smtpCode, string? text)
    {
        var now = DateTimeOffset.UtcNow;
        var sample = (text ?? "").Trim();
        if (sample.Length > 120) sample = sample[..120] + "…";

        // updateValueFactory may run more than once — no in-place side effects.
        _rows.AddOrUpdate(smtpCode,
            _ => new ObservedResponseRow
            {
                SmtpCode = smtpCode,
                SampleText = sample,
                Count = 1,
                FirstSeen = now,
                LastSeen = now,
                Classification = Classify(smtpCode, sample),
                RecommendedAction = Recommend(smtpCode, sample)
            },
            (_, existing) => new ObservedResponseRow
            {
                SmtpCode = existing.SmtpCode,
                SampleText = string.IsNullOrEmpty(sample) ? existing.SampleText : sample,
                Count = existing.Count + 1,
                FirstSeen = existing.FirstSeen,
                LastSeen = now,
                Classification = Classify(smtpCode, string.IsNullOrEmpty(sample) ? existing.SampleText : sample),
                RecommendedAction = Recommend(smtpCode, string.IsNullOrEmpty(sample) ? existing.SampleText : sample)
            });
    }

    public IReadOnlyList<ObservedResponseRow> Snapshot()
        => _rows.Values
            .OrderByDescending(r => r.Count)
            .Select(r => new ObservedResponseRow
            {
                SmtpCode = r.SmtpCode,
                SampleText = r.SampleText,
                Count = r.Count,
                FirstSeen = r.FirstSeen,
                LastSeen = r.LastSeen,
                Classification = r.Classification,
                RecommendedAction = r.RecommendedAction
            })
            .ToList();

    public static ResponseClassification Classify(int code, string text)
    {
        var t = text.ToLowerInvariant();
        if (code >= 200 && code < 300) return ResponseClassification.Success;
        if (code == 421 || code == 450 || code == 451 || code == 452)
        {
            if (t.Contains("greylist") || t.Contains("graylist") || t.Contains("try again") ||
                t.Contains("try later") || t.Contains("deferred") || t.Contains("temporarily"))
                return ResponseClassification.Greylist;
            if (t.Contains("rate") || t.Contains("limit") || t.Contains("too many") || t.Contains("throttle"))
                return ResponseClassification.RateLimit;
            return ResponseClassification.TransientOther;
        }
        if (code >= 500) return ResponseClassification.Permanent;
        return ResponseClassification.Other;
    }

    public static string Recommend(int code, string text)
    {
        return Classify(code, text) switch
        {
            ResponseClassification.Greylist => "Počkat 5–15 min a zkusit znovu (zapněte greylist retry).",
            ResponseClassification.RateLimit => "Snížit tempo, zapnout jitter a burst+pause.",
            ResponseClassification.TransientOther => "Dočasná chyba – retry s progressive backoff.",
            ResponseClassification.Permanent => "Trvalá chyba – zkontrolovat From, DNS, reputaci, obsah.",
            ResponseClassification.Success => "OK",
            _ => "Zkontrolovat session log a nastavení serveru."
        };
    }

    public static bool IsGreylist(int code, string? text)
        => Classify(code, text ?? "") == ResponseClassification.Greylist;
}

/// <summary>
/// Vestavěná znalostní báze běžných limitů a filtrů.
/// GUI ji zobrazí jako read-only tabulku + možnost přidat vlastní poznámky.
/// </summary>
public static class FilterKnowledgeBase
{
    public sealed record Entry(
        string Provider,
        string TypicalLimit,
        string TypicalResponse,
        string Recommendation);

    public static IReadOnlyList<Entry> DefaultEntries { get; } =
    [
        new("Gmail / Google Workspace (osobní)", "cca 100–500 zpráv/den", "421, 454, „try again later“",
            "Šetrný režim, warm-up, nízký paralelismus, silná reputace domény."),
        new("Microsoft 365 / Outlook.com", "dle licence a reputace", "4xx, 550 5.7.1",
            "Respektovat 4xx, zkontrolovat SPF/DKIM/DMARC, neposílat najednou velké dávky."),
        new("Seznam.cz / Email.cz", "nižší stovky/den při nové IP", "421, 550",
            "Warm-up, jitter, ověřit, že IP není na RBL."),
        new("Shared hosting (běžný)", "100–300 zpráv/hodinu", "421, 550",
            "Burst+pause, max 2–5 paralelních spojení, delší interval."),
        new("Firemní Exchange / interní relay", "podle politiky organizace", "greylist + RBL",
            "Greylist retry 10–15 min, povolit interní IP, případně výjimku z filtru."),
        new("Obecné DNSBL (Spamhaus, Barracuda…)", "–", "554, 550 „blocked“, „listed“",
            "Před startem zkontrolovat source IP. Při listingu řešit delist a reputaci."),
        new("Greylisting (obecně)", "první pokus odmítnut", "451, 452, „greylist“, „try again“",
            "Zapnout detekci greylistu a odložený retry 5–15 minut."),
        new("Obsahové filtry (SpamAssassin apod.)", "skóre nad prahem", "550 5.7.1, „spam“",
            "Testovat s reálným obsahem, vyhnout se spam trigger slovům, platné odkazy.")
    ];
}
