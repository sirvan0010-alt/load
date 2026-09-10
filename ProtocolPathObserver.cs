using System.Collections.Concurrent;
using System.Text;

namespace MailLoadTester;

/// <summary>
/// Sleduje skutečnou SMTP komunikaci (C:/S: řádky z MailKit IProtocolLogger)
/// a mapuje je na kroky pipeline (EHLO, STARTTLS, AUTH, MAIL FROM, RCPT TO, DATA, QUIT).
/// Thread-safe. Runner si po Rent/Send vyzvedne nové události přes Drain().
/// </summary>
public sealed class ProtocolPathObserver : MailKit.IProtocolLogger
{
    private readonly ConcurrentQueue<(DeliveryStepKind Step, bool? Ok, string Detail)> _events = new();
    // Plain field + lock (not AsyncLocal) for the pending-command/response pairing.
    // One observer instance is created per SmtpClient/connection (see
    // SmtpConnectionPool.RentAsync), so this only needs to be safe against
    // whichever thread MailKit happens to invoke LogClient/LogServer on for that
    // one connection — it does not need to track separate logical async flows.
    private readonly object _pendingLock = new();
    private DeliveryStepKind? _pendingClientStep;

    public MailKit.IAuthenticationSecretDetector? AuthenticationSecretDetector { get; set; }

    public void LogConnect(Uri uri)
    {
        _events.Enqueue((DeliveryStepKind.TcpConnect, true, uri.ToString()));
        lock (_pendingLock) _pendingClientStep = null;
    }

    public void LogClient(byte[] buffer, int offset, int count)
    {
        var text = Decode(buffer, offset, count).Trim();
        if (string.IsNullOrEmpty(text)) return;

        var upper = text.Length > 12 ? text[..12].ToUpperInvariant() : text.ToUpperInvariant();
        DeliveryStepKind? step = null;
        if (upper.StartsWith("EHLO") || upper.StartsWith("HELO"))
            step = DeliveryStepKind.Ehlo;
        else if (upper.StartsWith("STARTTLS"))
            step = DeliveryStepKind.StartTls;
        else if (upper.StartsWith("AUTH"))
            step = DeliveryStepKind.Auth;
        else if (upper.StartsWith("MAIL FROM"))
            step = DeliveryStepKind.MailFrom;
        else if (upper.StartsWith("RCPT TO"))
            step = DeliveryStepKind.RcptTo;
        else if (upper.StartsWith("DATA") && !upper.StartsWith("DATAL")) // DATA
            step = DeliveryStepKind.Data;
        else if (upper.StartsWith("QUIT"))
            step = DeliveryStepKind.Quit;

        if (step.HasValue)
        {
            lock (_pendingLock)
            {
                _pendingClientStep = step;
                // Příkaz odeslán – zatím bez výsledku (null = in progress).
                _events.Enqueue((step.Value, null, text.Length > 80 ? text[..80] : text));
            }
        }
    }

    public void LogServer(byte[] buffer, int offset, int count)
    {
        var text = Decode(buffer, offset, count).Trim();
        if (string.IsNullOrEmpty(text)) return;

        // SMTP odpověď: první 3 znaky = kód
        bool ok = text.Length >= 3 && text[0] == '2';
        bool fail = text.Length >= 3 && (text[0] == '4' || text[0] == '5');

        DeliveryStepKind? pending;
        lock (_pendingLock)
        {
            pending = _pendingClientStep;
            if (pending.HasValue && (ok || fail))
            {
                _events.Enqueue((pending.Value, ok, text.Length > 100 ? text[..100] : text));
                if (fail)
                    _events.Enqueue((DeliveryStepKind.Error, false, text.Length > 100 ? text[..100] : text));
                _pendingClientStep = null;
            }
        }
    }

    /// <summary>Vyzvedne frontu událostí (FIFO) od posledního Drain.</summary>
    public IReadOnlyList<(DeliveryStepKind Step, bool? Ok, string Detail)> Drain()
    {
        var list = new List<(DeliveryStepKind, bool?, string)>();
        while (_events.TryDequeue(out var e))
            list.Add(e);
        return list;
    }

    public void Clear()
    {
        while (_events.TryDequeue(out _)) { }
        lock (_pendingLock) _pendingClientStep = null;
    }

    public void Dispose() { }

    private static string Decode(byte[] buffer, int offset, int count)
    {
        try
        {
            return Encoding.UTF8.GetString(buffer, offset, count)
                .Replace("\r", "").Replace("\n", " ").Trim();
        }
        catch
        {
            return "";
        }
    }
}

/// <summary>
/// Spojí session file logger + path observer do jednoho IProtocolLogger pro SmtpClient.
/// </summary>
public sealed class CompositeProtocolLogger : MailKit.IProtocolLogger
{
    private readonly MailKit.IProtocolLogger? _a;
    private readonly MailKit.IProtocolLogger? _b;

    public CompositeProtocolLogger(MailKit.IProtocolLogger? a, MailKit.IProtocolLogger? b)
    {
        _a = a;
        _b = b;
    }

    public MailKit.IAuthenticationSecretDetector? AuthenticationSecretDetector
    {
        get => _a?.AuthenticationSecretDetector ?? _b?.AuthenticationSecretDetector;
        set
        {
            if (_a != null) _a.AuthenticationSecretDetector = value;
            if (_b != null) _b.AuthenticationSecretDetector = value;
        }
    }

    public void LogConnect(Uri uri)
    {
        _a?.LogConnect(uri);
        _b?.LogConnect(uri);
    }

    public void LogClient(byte[] buffer, int offset, int count)
    {
        _a?.LogClient(buffer, offset, count);
        _b?.LogClient(buffer, offset, count);
    }

    public void LogServer(byte[] buffer, int offset, int count)
    {
        _a?.LogServer(buffer, offset, count);
        _b?.LogServer(buffer, offset, count);
    }

    public void Dispose()
    {
        (_a as IDisposable)?.Dispose();
        (_b as IDisposable)?.Dispose();
    }
}
