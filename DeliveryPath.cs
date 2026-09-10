namespace MailLoadTester;

/// <summary>
/// Typ kroku v cestě odeslání jedné zprávy / session.
/// GUI z těchto hodnot skládá klikací pipeline.
/// </summary>
public enum DeliveryStepKind
{
    DnsMxLookup,
    TcpConnect,
    Ehlo,
    StartTls,
    Auth,
    MailFrom,
    RcptTo,
    Data,
    Quit,
    Error
}

/// <summary>
/// Jeden krok cesty odesílání.
/// </summary>
public sealed record DeliveryStep(
    DeliveryStepKind Kind,
    string Title,
    string Detail,
    int? SmtpCode,
    string? SmtpText,
    TimeSpan Duration,
    bool Success,
    DateTimeOffset Timestamp);

/// <summary>
/// Sleduje a skládá cestu odesílání pro GUI a session log.
/// Jedna instance = jedna zpráva nebo jedna session (podle použití).
/// </summary>
public sealed class DeliveryPathTracker
{
    private readonly List<DeliveryStep> _steps = new();
    private readonly object _lock = new();

    public IReadOnlyList<DeliveryStep> Steps
    {
        get { lock (_lock) return _steps.ToList(); }
    }

    public void Add(
        DeliveryStepKind kind,
        string title,
        string detail,
        int? smtpCode = null,
        string? smtpText = null,
        TimeSpan? duration = null,
        bool success = true)
    {
        lock (_lock)
        {
            _steps.Add(new DeliveryStep(
                kind,
                title,
                detail,
                smtpCode,
                smtpText,
                duration ?? TimeSpan.Zero,
                success,
                DateTimeOffset.UtcNow));
        }
    }

    public string ToSummaryLine()
    {
        lock (_lock)
        {
            if (_steps.Count == 0) return "(zatím žádné kroky)";
            return string.Join(" → ", _steps.Select(s =>
            {
                var mark = s.Success ? "✓" : "✗";
                return $"{s.Title}{mark}";
            }));
        }
    }

    /// <summary>
    /// Lidsky čitelný text pro session log / report.
    /// </summary>
    public string ToDetailedText()
    {
        lock (_lock)
        {
            var lines = new List<string> { "=== Cesta odesílání ===" };
            foreach (var s in _steps)
            {
                var code = s.SmtpCode.HasValue ? $" [{s.SmtpCode}]" : "";
                var dur = s.Duration > TimeSpan.Zero ? $" ({s.Duration.TotalMilliseconds:F0} ms)" : "";
                lines.Add($"{(s.Success ? "[OK]" : "[!!]")} {s.Title}{code}{dur}: {s.Detail}");
                if (!string.IsNullOrWhiteSpace(s.SmtpText))
                    lines.Add($"      {s.SmtpText}");
            }
            return string.Join(Environment.NewLine, lines);
        }
    }
}

/// <summary>
/// EHLO schopnosti serveru – pro tabulku v GUI.
/// </summary>
public sealed class EhloCapabilities
{
    public bool StartTls { get; init; }
    public IReadOnlyList<string> AuthMechanisms { get; init; } = Array.Empty<string>();
    public long? MaxSize { get; init; }
    public bool Pipelining { get; init; }
    public bool EightBitMime { get; init; }
    public bool SmtpUtf8 { get; init; }
    public bool Chunking { get; init; }
    public IReadOnlyList<string> RawLines { get; init; } = Array.Empty<string>();

    public IReadOnlyList<(string Name, string Value)> ToTableRows()
    {
        var rows = new List<(string, string)>
        {
            ("STARTTLS", StartTls ? "Ano" : "Ne"),
            ("AUTH", AuthMechanisms.Count > 0 ? string.Join(", ", AuthMechanisms) : "Ne"),
            ("SIZE", MaxSize.HasValue ? MaxSize.Value.ToString("N0") + " B" : "Neuvedeno"),
            ("PIPELINING", Pipelining ? "Ano" : "Ne"),
            ("8BITMIME", EightBitMime ? "Ano" : "Ne"),
            ("SMTPUTF8", SmtpUtf8 ? "Ano" : "Ne"),
            ("CHUNKING", Chunking ? "Ano" : "Ne")
        };
        return rows;
    }
}
