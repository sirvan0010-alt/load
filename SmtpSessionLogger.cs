using System.Text;

namespace MailLoadTester;

/// <summary>
/// Logs SMTP session traffic to a file for debugging.
/// Thread-safe, best-effort (does not throw on IO errors).
/// </summary>
public sealed class SmtpSessionLogger : IDisposable
{
    private readonly string _path;
    private readonly object _lock = new();
    private readonly StringBuilder _buffer = new();
    private readonly Timer _flushTimer;
    private readonly object _flushLock = new();

    public SmtpSessionLogger(string path, bool append = false)
    {
        _path = path;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        if (append && File.Exists(_path))
            File.AppendAllText(_path, $"\r\n=== Nový pokus (auto-restart): {DateTime.Now:O} ===\r\n\r\n");
        else
            File.WriteAllText(_path, $"=== MailLoadTester SMTP Session Log ===\r\nStarted: {DateTime.Now:O}\r\n\r\n");
        _flushTimer = new Timer(_ => Flush(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public void LogClient(string command)
    {
        lock (_lock) _buffer.AppendLine($"[{DateTime.Now:HH:mm:ss.fff}] C: {command}");
    }

    public void LogServer(string response)
    {
        lock (_lock) _buffer.AppendLine($"[{DateTime.Now:HH:mm:ss.fff}] S: {response}");
    }

    public void LogInfo(string info)
    {
        lock (_lock) _buffer.AppendLine($"[{DateTime.Now:HH:mm:ss.fff}] I: {info}");
    }

    private const int MaxBufferedChars = 4 * 1024 * 1024; // ~4M chars safety cap
    private bool _droppedWarningLogged;

    private void Flush()
    {
        // Timer callbacks may overlap with Dispose(). Serialize the complete
        // snapshot/write/requeue transaction so two flushes never append
        // concurrently or reorder/requeue the same buffer.
        lock (_flushLock)
        {
            string text;
            lock (_lock)
            {
                if (_buffer.Length == 0) return;
                text = _buffer.ToString();
                _buffer.Clear();
            }

            try
            {
                File.AppendAllText(_path, text);
            }
            catch (Exception ex)
            {
                lock (_lock)
                {
                    var combined = text + _buffer;
                    if (combined.Length > MaxBufferedChars)
                    {
                        combined = combined[^MaxBufferedChars..];
                        if (!_droppedWarningLogged)
                        {
                            _droppedWarningLogged = true;
                            combined = "[... starší část session logu zahozena, zápis na disk selhává ...]\r\n" + combined;
                        }
                    }
                    _buffer.Clear();
                    _buffer.Append(combined);
                }

                // Never throw into the SMTP/timer callback, but retain a useful
                // diagnostic in the debugger/event stream when one is attached.
                System.Diagnostics.Debug.WriteLine($"SmtpSessionLogger.Flush failed: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        _flushTimer.Dispose();
        Flush();
    }
}

/// <summary>
/// Adaptér na MailKit.IProtocolLogger — teprve tohle zajistí, že se do logu
/// dostane SKUTEČNÁ C:/S: SMTP komunikace, ne jen pár ručně psaných hlášek.
/// Nastav na SmtpClient.ProtocolLogger před ConnectAsync.
/// </summary>
public sealed class SessionProtocolLogger : MailKit.IProtocolLogger
{
    readonly SmtpSessionLogger _target;

    public SessionProtocolLogger(SmtpSessionLogger target) => _target = target;

    public MailKit.IAuthenticationSecretDetector? AuthenticationSecretDetector { get; set; }

    public void LogConnect(Uri uri) => _target.LogInfo($"Connect: {uri}");

    public void LogClient(byte[] buffer, int offset, int count) =>
        _target.LogClient(Decode(buffer, offset, count));

    public void LogServer(byte[] buffer, int offset, int count) =>
        _target.LogServer(Decode(buffer, offset, count));

    static string Decode(byte[] buffer, int offset, int count) =>
        System.Text.Encoding.ASCII.GetString(buffer, offset, count).TrimEnd('\r', '\n');

    public void Dispose() { /* SmtpSessionLogger má vlastní lifecycle, tady nic nezavíráme */ }
}
