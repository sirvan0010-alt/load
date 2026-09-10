using MailLoadTester;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace MailLoadTester.Tests;

public sealed class SmtpTestRunnerTests
{
    [Fact]
    public async Task SuccessfulSend_ReportsSentAndLatency()
    {
        await using var server = new ScriptedSmtpServer(SmtpBehavior.Accept);
        var runner = new SmtpTestRunner();
        var progress = new Progress<ProgressUpdate>();

        var result = await runner.RunAsync(CreateOptions(server.Port, messageCount: 1, maxRetries: 0), progress, CancellationToken.None);

        Assert.Equal(1, result.Sent);
        Assert.Equal(0, result.Failed);
        Assert.False(result.Cancelled);
        Assert.Equal(0, result.Retries);
        Assert.True(result.AvgLatencyMs >= 0);
        Assert.Equal(1, server.MessagesAccepted);
    }

    [Fact]
    public async Task Permanent5xx_DoesNotRetry()
    {
        await using var server = new ScriptedSmtpServer(SmtpBehavior.FailPermanent);
        var runner = new SmtpTestRunner();

        var result = await runner.RunAsync(CreateOptions(server.Port, messageCount: 1, maxRetries: 3),
            new Progress<ProgressUpdate>(), CancellationToken.None);

        Assert.Equal(0, result.Sent);
        Assert.Equal(1, result.Failed);
        Assert.Equal(0, result.Retries);
        Assert.Equal(1, result.Smtp5xx);
        Assert.False(result.Cancelled);
    }

    [Fact]
    public async Task Transient4xx_RetriesAndThenFails()
    {
        await using var server = new ScriptedSmtpServer(SmtpBehavior.FailTransient);
        var runner = new SmtpTestRunner();

        var result = await runner.RunAsync(CreateOptions(server.Port, messageCount: 1, maxRetries: 1),
            new Progress<ProgressUpdate>(), CancellationToken.None);

        Assert.Equal(0, result.Sent);
        Assert.Equal(1, result.Failed);
        Assert.Equal(1, result.Retries);
        Assert.True(result.Smtp4xx >= 1);
        Assert.False(result.Cancelled);
        Assert.True(server.ConnectionCount >= 2, "Retry should use a new SMTP connection after the failed send.");
    }

    [Fact]
    public async Task Cancellation_ReturnsPartialResults()
    {
        await using var server = new ScriptedSmtpServer(SmtpBehavior.DelayData);
        var runner = new SmtpTestRunner();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));

        var result = await runner.RunAsync(CreateOptions(server.Port, messageCount: 20, maxRetries: 0, maxConcurrency: 2),
            new Progress<ProgressUpdate>(), cts.Token);

        Assert.True(result.Cancelled);
        Assert.True(result.Sent + result.Failed < result.Requested,
            $"Expected partial results, got Sent={result.Sent}, Failed={result.Failed}, Requested={result.Requested}.");
    }

    private static MailTestOptions CreateOptions(int port, int messageCount, int maxRetries, int maxConcurrency = 1) => new(
        From: "sender@example.test",
        Recipients: new[] { "recipient@example.test" },
        SmtpHost: "127.0.0.1",
        Port: port,
        Security: SmtpSecurity.None,
        UseAuthentication: false,
        Username: "",
        Password: "",
        MessageCount: messageCount,
        IntervalMs: 0,
        BatchMode: false,
        BatchSize: 1,
        BatchPauseSeconds: 1,
        MaxConcurrency: maxConcurrency,
        Subject: "MailLoadTester test",
        Body: "Integration test",
        DisplayName: "MailLoadTester",
        RandomTestData: false,
        TestMode: false,
        AllowedDomains: "",
        HtmlBody: false,
        Attachments: Array.Empty<string>(),
        CustomHeaders: new Dictionary<string, string>(),
        IgnoreCertificateErrors: false,
        MaxRetries: maxRetries,
        DryRun: false);

    private enum SmtpBehavior { Accept, FailPermanent, FailTransient, DelayData }

    private sealed class ScriptedSmtpServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _acceptLoop;
        private readonly SmtpBehavior _behavior;
        private int _connections;
        private int _accepted;

        public int Port { get; }
        public int ConnectionCount => Volatile.Read(ref _connections);
        public int MessagesAccepted => Volatile.Read(ref _accepted);

        public ScriptedSmtpServer(SmtpBehavior behavior)
        {
            _behavior = behavior;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _acceptLoop = AcceptLoopAsync();
        }

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                    Interlocked.Increment(ref _connections);
                    _ = HandleClientAsync(client, _cts.Token);
                }
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
            catch (ObjectDisposedException) { }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
        {
            using (client)
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, leaveOpen: true);
            await using var writer = new StreamWriter(stream, Encoding.ASCII, 4096, leaveOpen: true)
            {
                NewLine = "\r\n",
                AutoFlush = true
            };

            await writer.WriteLineAsync("220 MailLoadTester integration test");

            var inData = false;
            while (!ct.IsCancellationRequested)
            {
                string? line;
                try { line = await reader.ReadLineAsync(ct); }
                catch (OperationCanceledException) { break; }
                if (line is null) break;

                if (inData)
                {
                    if (line == ".")
                    {
                        inData = false;
                        switch (_behavior)
                        {
                            case SmtpBehavior.Accept:
                                Interlocked.Increment(ref _accepted);
                                await writer.WriteLineAsync("250 2.0.0 OK");
                                break;
                            case SmtpBehavior.FailPermanent:
                                await writer.WriteLineAsync("550 5.7.1 Permanent test failure");
                                break;
                            case SmtpBehavior.FailTransient:
                                await writer.WriteLineAsync("451 4.3.0 Temporary test failure");
                                break;
                            case SmtpBehavior.DelayData:
                                await Task.Delay(TimeSpan.FromSeconds(10), ct);
                                break;
                        }
                    }
                    continue;
                }

                var command = line.Length >= 4 ? line[..4].ToUpperInvariant() : line.ToUpperInvariant();
                if (command.StartsWith("EHLO") || command.StartsWith("HELO"))
                {
                    await writer.WriteLineAsync("250-localhost");
                    await writer.WriteLineAsync("250 PIPELINING");
                }
                else if (command.StartsWith("MAIL") || command.StartsWith("RCPT"))
                    await writer.WriteLineAsync("250 2.1.0 OK");
                else if (command.StartsWith("DATA"))
                {
                    inData = true;
                    await writer.WriteLineAsync("354 End data with <CR><LF>.<CR><LF>");
                }
                else if (command.StartsWith("RSET"))
                    await writer.WriteLineAsync("250 OK");
                else if (command.StartsWith("NOOP"))
                    await writer.WriteLineAsync("250 OK");
                else if (command.StartsWith("QUIT"))
                {
                    await writer.WriteLineAsync("221 Bye");
                    break;
                }
                else
                    await writer.WriteLineAsync("250 OK");
            }
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _listener.Stop();
            try { await _acceptLoop; } catch { }
            _cts.Dispose();
        }
    }
}
