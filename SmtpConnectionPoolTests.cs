using MailKit.Net.Smtp;
using MailLoadTester;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace MailLoadTester.Tests;

public sealed class SmtpConnectionPoolTests
{
    [Fact]
    public async Task HealthyClient_IsReturnedAndReused()
    {
        await using var server = new FakeSmtpServer();
        var options = CreateOptions(server.Port, maxConcurrency: 1);
        await using var pool = new SmtpConnectionPool(options);

        var first = await pool.RentAsync(CancellationToken.None);
        Assert.True(first.IsConnected);
        Assert.Equal(1, pool.CreatedCount);

        pool.Return(first);
        var second = await pool.RentAsync(CancellationToken.None);

        Assert.Same(first, second);
        Assert.Equal(1, pool.CreatedCount);
        pool.Return(second);
    }

    [Fact]
    public async Task DiscardedClient_IsNotReused()
    {
        await using var server = new FakeSmtpServer();
        var options = CreateOptions(server.Port, maxConcurrency: 1);
        await using var pool = new SmtpConnectionPool(options);

        var first = await pool.RentAsync(CancellationToken.None);
        pool.Discard(first);

        var second = await pool.RentAsync(CancellationToken.None);

        Assert.NotSame(first, second);
        Assert.Equal(2, pool.CreatedCount);
        Assert.True(second.IsConnected);
        pool.Return(second);
    }


    [Fact]
    public async Task CancelledRent_DoesNotConsumePoolSlot()
    {
        await using var server = new FakeSmtpServer();
        var options = CreateOptions(server.Port, maxConcurrency: 1);
        await using var pool = new SmtpConnectionPool(options);

        var held = await pool.RentAsync(CancellationToken.None);
        using var cts = new CancellationTokenSource();
        var waiting = pool.RentAsync(cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);

        pool.Return(held);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var next = await pool.RentAsync(timeout.Token);
        Assert.True(next.IsConnected);
        pool.Return(next);
    }

    [Fact]
    public async Task ReturnAfterPoolDispose_DoesNotThrow()
    {
        await using var server = new FakeSmtpServer();
        var options = CreateOptions(server.Port, maxConcurrency: 1);
        var pool = new SmtpConnectionPool(options);
        var client = await pool.RentAsync(CancellationToken.None);

        await pool.DisposeAsync();

        var exception = Record.Exception(() => pool.Return(client));
        Assert.Null(exception);
    }

    [Fact]
    public async Task DuplicateReturn_DoesNotInflateConcurrency()
    {
        await using var server = new FakeSmtpServer();
        var options = CreateOptions(server.Port, maxConcurrency: 1);
        await using var pool = new SmtpConnectionPool(options);

        var first = await pool.RentAsync(CancellationToken.None);
        pool.Return(first);
        pool.Return(first); // must be ignored, not create a second permit/queue entry

        var held = await pool.RentAsync(CancellationToken.None);
        var waiting = pool.RentAsync(CancellationToken.None);
        await Task.Delay(100);
        Assert.False(waiting.IsCompleted);

        pool.Return(held);
        var second = await waiting.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.NotNull(second);
        pool.Return(second);
    }

    [Fact]
    public async Task IdleClient_HealthCheckUsesNoOp()
    {
        await using var server = new FakeSmtpServer();
        var options = CreateOptions(server.Port, maxConcurrency: 1) with
        {
            IdleConnectionHealthCheckSeconds = 0
        };
        await using var pool = new SmtpConnectionPool(options);

        var client = await pool.RentAsync(CancellationToken.None);
        pool.Return(client);

        var reused = await pool.RentAsync(CancellationToken.None);

        Assert.Same(client, reused);
        Assert.True(server.NoOpCount >= 1, $"Expected NOOP health-check, got {server.NoOpCount}.");
        pool.Return(reused);
    }

    private static MailTestOptions CreateOptions(int port, int maxConcurrency) => new(
        From: "sender@example.test",
        Recipients: new[] { "recipient@example.test" },
        SmtpHost: "127.0.0.1",
        Port: port,
        Security: SmtpSecurity.None,
        UseAuthentication: false,
        Username: "",
        Password: "",
        MessageCount: 1,
        IntervalMs: 0,
        BatchMode: false,
        BatchSize: 1,
        BatchPauseSeconds: 0,
        MaxConcurrency: maxConcurrency,
        Subject: "test",
        Body: "test",
        DisplayName: "test",
        RandomTestData: false,
        TestMode: false,
        AllowedDomains: "",
        HtmlBody: false,
        Attachments: Array.Empty<string>(),
        CustomHeaders: new Dictionary<string, string>(),
        IgnoreCertificateErrors: false,
        MaxRetries: 0,
        DryRun: false);

    private sealed class FakeSmtpServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _acceptLoop;

        public int Port { get; }
        public int NoOpCount => Volatile.Read(ref _noopCount);
        private int _noopCount;

        public FakeSmtpServer()
        {
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
                    _ = HandleClientAsync(client, _cts.Token);
                }
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
            catch (ObjectDisposedException) { }
        }

        private static async Task HandleClientAsync(TcpClient client, CancellationToken ct)
        {
            using (client)
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, leaveOpen: true);
            await using var writer = new StreamWriter(stream, Encoding.ASCII, 4096, leaveOpen: true) { NewLine = "\r\n", AutoFlush = true };

            await writer.WriteLineAsync("220 MailLoadTester Test SMTP");
            var inData = false;
            while (!ct.IsCancellationRequested && client.Connected)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line is null) break;

                if (inData)
                {
                    if (line == ".")
                    {
                        inData = false;
                        await writer.WriteLineAsync("250 2.0.0 OK");
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
                {
                    Interlocked.Increment(ref _noopCount);
                    await writer.WriteLineAsync("250 OK");
                }
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
    [Fact]
    public async Task DisposeKeepsLeasedPermitOwnedUntilReturn()
    {
        await using var server = new FakeSmtpServer();
        var options = CreateOptions(server.Port, maxConcurrency: 1);
        var pool = new SmtpConnectionPool(options);

        var held = await pool.RentAsync(CancellationToken.None);
        var waiting = pool.RentAsync(CancellationToken.None);

        await pool.DisposeAsync();
        await Task.Delay(50);
        Assert.False(waiting.IsCompleted);

        // Shutdown must not steal the leased client's ownership/permit.
        pool.Return(held);

        await Assert.ThrowsAnyAsync<ObjectDisposedException>(
            async () => await waiting.WaitAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task DisposeWhileRentIsWaiting_DoesNotLeaveWaiterBlocked()
    {
        await using var server = new FakeSmtpServer();
        var options = CreateOptions(server.Port, maxConcurrency: 1);
        var pool = new SmtpConnectionPool(options);

        var held = await pool.RentAsync(CancellationToken.None);
        var waiting = pool.RentAsync(CancellationToken.None);

        await pool.DisposeAsync();
        pool.Return(held);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAnyAsync<ObjectDisposedException>(
            async () => await waiting.WaitAsync(timeout.Token));
    }

}
