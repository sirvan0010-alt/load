namespace MailLoadTester;

public enum SmtpAuthMethod { Auto, Plain, Login, CramMd5, ScramSha1, Ntlm, OAuth2 }
public enum IpVersionPreference { Any, IPv4Only, IPv6Only, DualStack }

public sealed record MailTestOptions(
    string From,
    IReadOnlyList<string> Recipients,
    string SmtpHost,
    int Port,
    SmtpSecurity Security,
    bool UseAuthentication,
    string Username,
    string Password,
    int MessageCount,
    int IntervalMs,
    bool BatchMode,
    int BatchSize,
    int BatchPauseSeconds,
    int MaxConcurrency,
    string Subject,
    string Body,
    string DisplayName,
    bool RandomTestData,
    bool TestMode,
    string AllowedDomains,
    bool HtmlBody,
    IReadOnlyList<string> Attachments,
    IReadOnlyDictionary<string, string> CustomHeaders,
    bool IgnoreCertificateErrors,
    int MaxRetries,
    bool DryRun,
    // Tier 2/3
    bool UseSocks5Proxy = false,
    string ProxyHost = "",
    int ProxyPort = 1080,
    string ProxyUsername = "",
    string ProxyPassword = "",
    /// <summary>Seznam proxy pro rotaci (řádky/středník). Formát socks5://host:1080 nebo http://user:pass@host:8080.</summary>
    string ProxyList = "",
    bool ProxyListRandom = false,
    /// <summary>Minuty vyřazení proxy/IP po detekci banu.</summary>
    int ProxyBanMinutes = 15,
    bool DirectMxDelivery = false,
    bool PreWarmConnections = false,
    bool UseAdaptiveConcurrency = false,
    bool UseCircuitBreaker = false,
    int CircuitBreakerThreshold = 5,
    /// <summary>Velikost sliding okna pro % chybovost (0 = jen klasický consecutive režim).</summary>
    int CircuitBreakerWindowSize = 100,
    /// <summary>Práh chybovosti v okně v % (např. 90 = otevřít při ≥90 % selhání).</summary>
    double CircuitBreakerFailurePercent = 90.0,
    bool EnableDashboard = false,
    int DashboardPort = 5000,
    // Max brutal
    SmtpAuthMethod AuthMethod = SmtpAuthMethod.Auto,
    string SourceIp = "",
    /// <summary>Volitelný IPv6 prefix pro rotaci source adres (např. 2001:db8:85a3:0::). Prázdné = vypnuto.</summary>
    string Ipv6Prefix = "",
    /// <summary>Délka IPv6 prefixu v bitech (typicky 64).</summary>
    int Ipv6PrefixLength = 64,
    /// <summary>Rotace IPv4 source: seznam IP nebo CIDR (192.0.2.10,192.0.2.11 nebo 192.0.2.0/28). Prázdné = vypnuto.</summary>
    string Ipv4Rotation = "",
    /// <summary>true = náhodný výběr z poolu, false = round-robin.</summary>
    bool Ipv4RotationRandom = false,
    string ClientCertificatePath = "",
    string ClientCertificatePassword = "",
    IReadOnlyList<string>? BccRecipients = null,
    IReadOnlyList<string>? CcRecipients = null,
    bool SmtpUtf8 = false,
    IReadOnlyList<string>? InlineAttachments = null,
    string WebhookUrl = "",
    int BandwidthLimitKbps = 0,
    IpVersionPreference IpVersion = IpVersionPreference.Any,
    int ConnectTimeoutMs = 20000,
    int ReadTimeoutMs = 20000,
    bool EnableSessionLog = false,
    string SessionLogPath = "",
    bool AutoRestartOnFailure = false,
    int AutoRestartMaxAttempts = 3,
    string? EmlTemplatePath = null,
    int IdleConnectionHealthCheckSeconds = 30,
    // Randomized message-content test options. Defaults preserve legacy behavior.
    bool UseBogusData = false,
    bool GenerateRandomHtml = false,
    bool GenerateRandomAttachments = false,
    int MaxRandomAttachments = 2,
    bool VarySubjectBodyPerMessage = false,
    // 0 = automatic safe size based on available memory and MaxConcurrency.
    int RandomAttachmentSizeMb = 0,
    // ========== Tempo a ochrana (2.9.x) ==========
    // Aggressive | Standard | Gentle | Custom (nebo Id z ProviderPresets)
    string PaceProfile = "Standard",
    bool EnableJitter = true,
    int JitterPercent = 15,
    bool EnableBurstMode = false,
    int BurstSize = 10,
    int BurstPauseSeconds = 30,
    bool EnableProgressiveBackoff = true,
    int BackoffAfterSuccesses = 50,
    double BackoffMultiplier = 1.5,
    int MaxIntervalMs = 60_000,
    bool EnablePerRecipientLimit = false,
    int MaxMessagesPerRecipient = 20,
    int PerRecipientWindowMinutes = 60,
    bool EnableSendingTimeWindow = false,
    int SendingWindowFromHour = 8,
    int SendingWindowToHour = 18,
    bool EnableWarmup = false,
    string WarmupPhases = "50;150;500",
    bool DetectGreylist = true,
    int GreylistRetryMinutes = 10,
    int MaxGreylistRetries = 3,
    string ProviderPreset = "Custom",
    bool CheckRblBeforeStart = false,
    bool CollectObservedResponses = true);

public enum SmtpSecurity { None, StartTls, ImplicitTls }

public sealed record MailTestResult(
    int Requested,
    int Sent,
    int Failed,
    TimeSpan Elapsed,
    string LastError,
    double AvgLatencyMs,
    double MinLatencyMs,
    double MaxLatencyMs,
    double P50LatencyMs,
    double P95LatencyMs,
    double P99LatencyMs,
    double ThroughputPerSec,
    bool Cancelled = false,
    double ActiveThroughputPerSec = 0,
    int Retries = 0,
    int Smtp4xx = 0,
    int Smtp5xx = 0,
    int Timeouts = 0,
    int PoolConnections = 0,
    int AdaptiveConcurrency = 0,
    bool CircuitBreakerOpen = false,
    IReadOnlyList<string>? MxHosts = null,
    int AutoRestartAttempts = 0,
    string? SessionLogFile = null);

public static class Validation
{
    public static bool IsValidEmail(string value)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            var addr = new System.Net.Mail.MailAddress(value);
            return addr.Address == value.Trim();
        }
        catch { return false; }
    }

    public static string[] ParseDomains(string raw) => raw
        .Split(new[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(x => x.Trim().TrimStart('@').ToLowerInvariant())
        .Where(x => x.Length > 0)
        .Distinct()
        .ToArray();

    public static IReadOnlyDictionary<string, string> ParseHeaders(string raw)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(raw)) return dict;
        foreach (var line in raw.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var idx = line.IndexOf(':');
            if (idx <= 0) throw new ArgumentException($"Invalid header line: {line}");
            var key = line[..idx].Trim();
            var val = line[(idx + 1)..].Trim();
            if (key.Length > 0) dict[key] = val;
        }
        return dict;
    }

    /// <summary>Odmítne cesty s ".." segmenty (základní ochrana proti path traversal).</summary>
    public static bool ContainsPathTraversal(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var normalized = path.Replace('\', '/');
        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Any(part => part == "..");
    }


    public static void Validate(MailTestOptions o)
    {
        if (!IsValidEmail(o.From))
            throw new ArgumentException("From musí být platný e-mailový formát.");
        if (o.Recipients.Count == 0 || o.Recipients.Any(x => !IsValidEmail(x)))
            throw new ArgumentException("Seznam příjemců obsahuje neplatný e-mail.");
        if (string.IsNullOrWhiteSpace(o.SmtpHost) && !o.DirectMxDelivery)
            throw new ArgumentException("SMTP server je povinný (pokud nepoužíváš Direct MX).");
        if (!string.IsNullOrEmpty(o.EmlTemplatePath) && ContainsPathTraversal(o.EmlTemplatePath))
            throw new ArgumentException("EML cesta nesmí obsahovat '..'.");
        if (!string.IsNullOrEmpty(o.SessionLogPath) && ContainsPathTraversal(o.SessionLogPath))
            throw new ArgumentException("Cesta session logu nesmí obsahovat '..'.");
        if (o.Port is < 1 or > 65535)
            throw new ArgumentException("Port musí být 1–65535.");
        if (o.MessageCount is < 1 or > 10000)
            throw new ArgumentException("Počet zpráv musí být 1–10000.");
        if (o.IntervalMs is < 0 or > 3_600_000)
            throw new ArgumentException("Interval je mimo povolený rozsah (0–3600000 ms).");
        if (o.UseAuthentication && string.IsNullOrWhiteSpace(o.Username))
            throw new ArgumentException("U SMTP autentizace je povinné uživatelské jméno.");
        if (o.UseAuthentication && string.IsNullOrEmpty(o.Password) && o.AuthMethod != SmtpAuthMethod.OAuth2)
            throw new ArgumentException("U SMTP autentizace je povinné heslo.");
        if (o.MaxConcurrency is < 1 or > 20)
            throw new ArgumentException("Paralelismus musí být 1–20.");
        if (o.MaxRetries is < 0 or > 5)
            throw new ArgumentException("Počet opakování musí být 0–5.");
        if (o.Security == SmtpSecurity.ImplicitTls && o.Port != 465)
            throw new ArgumentException("Implicit TLS je podporováno na portu 465.");
        if (o.Security == SmtpSecurity.StartTls && o.Port == 465)
            throw new ArgumentException("Port 465 používá implicit TLS, nikoli STARTTLS.");
        foreach (var header in o.CustomHeaders)
        {
            if (!IsSafeCustomHeaderName(header.Key))
                throw new ArgumentException($"Nepovolená vlastní hlavička: {header.Key}");
            if (header.Value.Contains('\r') || header.Value.Contains('\n'))
                throw new ArgumentException($"Hodnota hlavičky {header.Key} obsahuje zakázaný nový řádek.");
        }
        if (o.BatchMode)
        {
            if (o.BatchSize is < 1 or > 1000)
                throw new ArgumentException("Velikost dávky musí být 1–1000.");
            if (o.BatchPauseSeconds is < 1 or > 86_400)
                throw new ArgumentException("Pauza mezi dávkami musí být 1–86400 sekund.");
        }
        if (o.DirectMxDelivery)
        {
            var domains = o.Recipients
                .Select(r => r[(r.LastIndexOf('@') + 1)..].TrimEnd('.').ToLowerInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (domains.Length > 1)
                throw new ArgumentException("Direct MX delivery vyžaduje příjemce z jedné domény; při více doménách by se všem zprávám použil MX server první domény.");
        }

        if (o.TestMode)
        {
            var allowed = ParseDomains(o.AllowedDomains);
            if (allowed.Length == 0)
                throw new ArgumentException("V Test mode zadejte alespoň jednu povolenou testovací doménu.");
            if (o.Recipients.Any(r =>
            {
                var at = r.LastIndexOf('@');
                if (at < 0 || at >= r.Length - 1) return true;
                return !allowed.Contains(r[(at + 1)..].ToLowerInvariant());
            }))
                throw new ArgumentException("Test mode: některý příjemce není v Allowed domains.");
        }
        foreach (var path in o.Attachments)
        {
            if (!File.Exists(path))
                throw new ArgumentException($"Příloha neexistuje: {path}");
        }
        foreach (var path in o.InlineAttachments ?? Array.Empty<string>())
        {
            if (!File.Exists(path))
                throw new ArgumentException($"Inline příloha neexistuje: {path}");
        }
        if (o.Subject.Length > 200)
            throw new ArgumentException("Předmět je příliš dlouhý (max 200 znaků, po MIME encodingu může být víc).");
        if (o.Body.Length > 10_000_000)
            throw new ArgumentException("Tělo zprávy je příliš velké (max 10 MB).");
        if (o.DisplayName.Length > 200)
            throw new ArgumentException("Display name je příliš dlouhý (max 200 znaků).");
        if (!string.IsNullOrWhiteSpace(o.ProxyList))
        {
            try { _ = ProxyClientFactory.ParseList(o.ProxyList); }
            catch (Exception ex) { throw new ArgumentException("ProxyList: " + ex.Message); }
        }

        if (o.UseSocks5Proxy)
        {
            if (string.IsNullOrWhiteSpace(o.ProxyHost))
                throw new ArgumentException("SOCKS5 proxy host je povinný.");
            if (o.ProxyPort is < 1 or > 65535)
                throw new ArgumentException("SOCKS5 proxy port musí být 1–65535.");
        }
        if (o.CircuitBreakerThreshold is < 1 or > 50)
            throw new ArgumentException("Circuit breaker threshold musí být 1–50.");
        if (o.CircuitBreakerWindowSize is < 0 or > 10_000)
            throw new ArgumentException("Circuit breaker window musí být 0–10000.");
        if (o.CircuitBreakerFailurePercent is < 1 or > 100)
            throw new ArgumentException("Circuit breaker failure % musí být 1–100.");
        if (o.DashboardPort is < 1 or > 65535)
            throw new ArgumentException("Dashboard port musí být 1–65535.");
        if (!string.IsNullOrEmpty(o.Ipv6Prefix))
        {
            if (!System.Net.IPAddress.TryParse(o.Ipv6Prefix, out var v6) ||
                v6.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
                throw new ArgumentException("Ipv6Prefix musí být platná IPv6 adresa/prefix.");
            if (o.Ipv6PrefixLength is < 1 or > 128)
                throw new ArgumentException("Ipv6PrefixLength musí být 1–128.");
        }

        if (!string.IsNullOrWhiteSpace(o.Ipv4Rotation))
        {
            try { _ = new IpV4Rotator(o.Ipv4Rotation); }
            catch (Exception ex) { throw new ArgumentException("Ipv4Rotation: " + ex.Message); }
        }

        if (!string.IsNullOrEmpty(o.SourceIp) && !System.Net.IPAddress.TryParse(o.SourceIp, out _))
            throw new ArgumentException("Source IP musí být platná IP adresa.");
        if (!string.IsNullOrEmpty(o.ClientCertificatePath) && !File.Exists(o.ClientCertificatePath))
            throw new ArgumentException("Client certificate neexistuje.");
        if (!string.IsNullOrEmpty(o.WebhookUrl) && !Uri.TryCreate(o.WebhookUrl, UriKind.Absolute, out _))
            throw new ArgumentException("Webhook URL musí být platná absolutní URL.");
        if (o.BandwidthLimitKbps is < 0 or > 1_000_000)
            throw new ArgumentException("Bandwidth limit musí být 0–1 000 000 kbps.");
        if (o.ConnectTimeoutMs is < 1000 or > 300_000)
            throw new ArgumentException("Connect timeout musí být 1000–300000 ms.");
        if (o.ReadTimeoutMs is < 1000 or > 300_000)
            throw new ArgumentException("Read timeout musí být 1000–300000 ms.");
        if (!string.IsNullOrEmpty(o.EmlTemplatePath) && !File.Exists(o.EmlTemplatePath))
            throw new ArgumentException("EML šablona neexistuje.");
        if (o.AutoRestartMaxAttempts is < 1 or > 10)
            throw new ArgumentException("Auto-restart max attempts musí být 1–10.");
        if (o.IdleConnectionHealthCheckSeconds is < 0 or > 86_400)
            throw new ArgumentException("Health-check idle spojení musí být 0–86400 sekund.");
        if (o.MaxRandomAttachments is < 1 or > 5)
            throw new ArgumentException("Maximální počet náhodných příloh musí být 1–5.");
        if (o.GenerateRandomAttachments && o.MaxRandomAttachments < 1)
            throw new ArgumentException("Počet náhodných příloh musí být alespoň 1.");
        if (o.RandomAttachmentSizeMb is < 0 or > 128)
            throw new ArgumentException("Velikost náhodné přílohy musí být 0 (Auto) až 128 MB.");

        // Tempo a ochrana
        if (o.JitterPercent is < 0 or > 50)
            throw new ArgumentException("Jitter musí být 0–50 %.");
        if (o.BurstSize is < 1 or > 100)
            throw new ArgumentException("Velikost burstu musí být 1–100.");
        if (o.BurstPauseSeconds is < 1 or > 3600)
            throw new ArgumentException("Pauza po burstu musí být 1–3600 s.");
        if (o.BackoffAfterSuccesses is < 1 or > 10_000)
            throw new ArgumentException("Backoff after successes musí být 1–10000.");
        if (o.BackoffMultiplier is < 1.0 or > 5.0)
            throw new ArgumentException("Backoff multiplier musí být 1.0–5.0.");
        if (o.MaxIntervalMs is < 100 or > 3_600_000)
            throw new ArgumentException("Max. interval musí být 100–3600000 ms.");
        if (o.MaxMessagesPerRecipient is < 1 or > 10_000)
            throw new ArgumentException("Max. zpráv na příjemce musí být 1–10000.");
        if (o.PerRecipientWindowMinutes is < 1 or > 10_080)
            throw new ArgumentException("Okno per-recipient musí být 1–10080 minut.");
        if (o.SendingWindowFromHour is < 0 or > 23 || o.SendingWindowToHour is < 0 or > 23)
            throw new ArgumentException("Hodiny časového okna musí být 0–23.");
        if (o.GreylistRetryMinutes is < 1 or > 120)
            throw new ArgumentException("Greylist retry musí být 1–120 minut.");
        if (o.MaxGreylistRetries is < 0 or > 20)
            throw new ArgumentException("Max. greylist retries musí být 0–20.");
    }

    static bool IsSafeCustomHeaderName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || !name.StartsWith("X-", StringComparison.OrdinalIgnoreCase))
            return false;
        foreach (var ch in name)
        {
            if (!(char.IsLetterOrDigit(ch) || ch == '-' || ch == '_'))
                return false;
        }
        return true;
    }

    public static string ExplainSmtpError(Exception ex)
    {
        if (ex is MailKit.Net.Smtp.SmtpCommandException sce)
        {
            var code = (int)sce.StatusCode;
            var hint = code switch
            {
                421 => "Služba dočasně nedostupná – zkuste později nebo snižte paralelismus.",
                450 => "Schránka dočasně nedostupná.",
                451 => "Chyba při zpracování – dočasná.",
                452 => "Nedostatek místa na serveru.",
                454 => "Dočasné selhání autentizace.",
                500 => "Syntaktická chyba příkazu.",
                501 => "Syntaktická chyba parametrů.",
                502 => "Příkaz není implementován.",
                503 => "Špatné pořadí příkazů.",
                504 => "Parametr není implementován.",
                535 => "Autentizace selhala (špatné jméno/heslo nebo metoda).",
                550 => "Schránka neexistuje nebo je odmítnuta.",
                551 => "Uživatel není lokální.",
                552 => "Překročena kvóta schránky.",
                553 => "Adresa příjemce je neplatná.",
                554 => "Transakce selhala (často policy/spam).",
                _ when code >= 400 && code < 500 => "Dočasná chyba (4xx) – lze opakovat.",
                _ when code >= 500 => "Trvalá chyba (5xx) – opakování nepomůže.",
                _ => ""
            };
            return string.IsNullOrEmpty(hint)
                ? $"SMTP {code}: {sce.Message}"
                : $"SMTP {code}: {sce.Message} — {hint}";
        }
        if (ex is MailKit.Security.AuthenticationException)
            return "Autentizace selhala: " + ex.Message;
        if (ex is MailKit.Net.Smtp.SmtpProtocolException)
            return "Chyba SMTP protokolu: " + ex.Message;
        if (ex is TimeoutException)
            return "Vypršel časový limit spojení se SMTP serverem.";
        if (ex is IOException)
            return "Síťová chyba: " + ex.Message;
        return ex.Message;
    }
}
