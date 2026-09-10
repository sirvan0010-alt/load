using MailKit.Net.Smtp;
using MailKit.Security;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Diagnostics;

namespace MailLoadTester;

public sealed class SmtpConnectionPool : IAsyncDisposable
{
    private readonly ConcurrentQueue<SmtpClient> _idle = new();
    private readonly ConcurrentDictionary<SmtpClient, byte> _all = new();
    private readonly ConcurrentDictionary<SmtpClient, byte> _leased = new();
    private readonly ConcurrentDictionary<SmtpClient, long> _idleSince = new();
    private readonly SemaphoreSlim _gate;
    private readonly MailTestOptions _options;
    private readonly SecureSocketOptions _socket;
    private readonly IPEndPoint? _localEp;
    private readonly IpV6Rotator? _ipv6Rotator;
    private readonly IpV4Rotator? _ipv4Rotator;
    private readonly bool _ipv4Random;
    private readonly ProxyRotator? _proxyRotator;
    private readonly ConcurrentDictionary<SmtpClient, ProxyEndpoint> _clientProxy = new();
    private readonly System.Security.Cryptography.X509Certificates.X509Certificate2? _clientCert;
    private readonly SmtpSessionLogger? _sessionLogger;
    private readonly ProtocolPathObserver? _pathObserverTemplate;
    private readonly ConcurrentDictionary<SmtpClient, ProtocolPathObserver> _pathObservers = new();
    private int _created;
    private int _disposed;
    // Counts RentAsync calls currently inside EnsureConnectedAsync for a brand-new
    // client — the window where _clientCert is attached to client.ClientCertificates
    // and actively used for the mTLS handshake, but before the client is visible in
    // _leased (MarkLeased only runs after EnsureConnectedAsync succeeds). DisposeAsync
    // must wait for this to drain to 0 before disposing _clientCert, or it could
    // dispose the certificate out from under an in-flight handshake on another thread.
    private int _inFlightConnects;
    private readonly object _lifecycleLock = new();

    public SmtpConnectionPool(MailTestOptions options, SmtpSessionLogger? sessionLogger = null, ProtocolPathObserver? pathObserver = null)
    {
        _options = options;
        _socket = SmtpConnectivityTester.ToSocketOptions(options.Security);
        _gate = new SemaphoreSlim(options.MaxConcurrency, options.MaxConcurrency);
        _sessionLogger = sessionLogger;
        _pathObserverTemplate = pathObserver;
        _localEp = IpBindingHelper.ResolveLocalEndPoint(options.SourceIp, options.IpVersion);
        _ipv6Rotator = !string.IsNullOrWhiteSpace(options.Ipv6Prefix)
            ? new IpV6Rotator(options.Ipv6Prefix, options.Ipv6PrefixLength <= 0 ? 64 : options.Ipv6PrefixLength)
            : null;
        _ipv4Rotator = !string.IsNullOrWhiteSpace(options.Ipv4Rotation)
            ? new IpV4Rotator(options.Ipv4Rotation)
            : null;
        _ipv4Random = options.Ipv4RotationRandom;
        if (!string.IsNullOrWhiteSpace(options.ProxyList))
        {
            var list = ProxyClientFactory.ParseList(options.ProxyList);
            if (list.Count > 0)
                _proxyRotator = new ProxyRotator(list, options.ProxyListRandom, TimeSpan.FromMinutes(Math.Max(1, options.ProxyBanMinutes)));
        }
        else if (options.UseSocks5Proxy && !string.IsNullOrWhiteSpace(options.ProxyHost))
        {
            var single = new ProxyEndpoint(ProxyType.Socks5, options.ProxyHost, options.ProxyPort,
                string.IsNullOrEmpty(options.ProxyUsername) ? null : options.ProxyUsername,
                string.IsNullOrEmpty(options.ProxyPassword) ? null : options.ProxyPassword);
            _proxyRotator = new ProxyRotator(new[] { single }, false, TimeSpan.FromMinutes(Math.Max(1, options.ProxyBanMinutes)));
        }
        _clientCert = ClientCertificateHelper.Load(options.ClientCertificatePath, options.ClientCertificatePassword);
    }

    public async Task PreWarmAsync(int count, CancellationToken ct)
    {
        count = Math.Min(count, _options.MaxConcurrency);
        var tasks = new List<Task<SmtpClient>>(count);
        for (int i = 0; i < count; i++)
            tasks.Add(RentAsync(ct));

        try
        {
            var clients = await Task.WhenAll(tasks).ConfigureAwait(false);
            foreach (var c in clients)
                Return(c);
        }
        catch
        {
            // If one warm-up connection fails, successfully rented clients must
            // still be returned; otherwise the pool can lose permits/connections.
            foreach (var task in tasks)
            {
                if (task.Status == TaskStatus.RanToCompletion)
                    Return(task.Result);
            }
            throw;
        }
    }

    public async Task<SmtpClient> RentAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(SmtpConnectionPool));

            while (_idle.TryDequeue(out var idleClient))
            {
                _idleSince.TryRemove(idleClient, out var idleSince);

                if (!idleClient.IsConnected)
                {
                    try
                    {
                        await EnsureConnectedAsync(idleClient, ct).ConfigureAwait(false);
                        if (Volatile.Read(ref _disposed) != 0)
                        {
                            Forget(idleClient);
                            throw new ObjectDisposedException(nameof(SmtpConnectionPool));
                        }
                        MarkLeased(idleClient);
                        return idleClient;
                    }
                    catch (OperationCanceledException) { Forget(idleClient); throw; }
                    catch (ObjectDisposedException) { Forget(idleClient); throw; }
                    catch (Exception ex)
                    {
                        // Classified as "expected fallback": a stale idle connection failing
                        // to reconnect is a normal, common occurrence (server closed it,
                        // network blip) — not swallowed silently, though, since a *pattern*
                        // of these (e.g. every idle client suddenly failing) can indicate a
                        // real outage worth seeing in the session log.
                        _sessionLogger?.LogInfo($"Idle client {idleClient.GetHashCode()} reconnect failed, discarding: {ex.Message}");
                        Forget(idleClient);
                    }
                }

                if (idleClient.IsConnected)
                {
                    // 0 = health-check vypnutý. >0 = NOOP po N sekundách nečinnosti.
                    // (Dříve 0 chybně znamenalo „vždy NOOP“.)
                    var shouldHealthCheck = false;
                    if (_options.IdleConnectionHealthCheckSeconds > 0 && idleSince > 0)
                    {
                        var idleSeconds = (Stopwatch.GetTimestamp() - idleSince) /
                            (double)Stopwatch.Frequency;
                        shouldHealthCheck = idleSeconds >= _options.IdleConnectionHealthCheckSeconds;
                    }

                    if (shouldHealthCheck)
                    {
                        try
                        {
                            await idleClient.NoOpAsync(ct).ConfigureAwait(false);
                            _sessionLogger?.LogInfo($"Idle health-check OK for client {idleClient.GetHashCode()}");
                        }
                        catch (OperationCanceledException)
                        {
                            Forget(idleClient);
                            throw;
                        }
                        catch
                        {
                            _sessionLogger?.LogInfo($"Idle health-check failed for client {idleClient.GetHashCode()}, discarding");
                            Forget(idleClient);
                            continue;
                        }
                    }

                    // DisposeAsync can race with an idle client between the health-check
                    // and this hand-off. Never return a client after shutdown has begun.
                    // Pair the shutdown check with the lease hand-off. A plain
                    // check-then-MarkLeased sequence has a TOCTOU window:
                    // DisposeAsync can set _disposed after the check but before the
                    // client becomes leased. Keep this transition atomic with shutdown.
                    bool shutdown;
                    lock (_lifecycleLock)
                    {
                        shutdown = Volatile.Read(ref _disposed) != 0;
                        if (!shutdown)
                        {
                            _sessionLogger?.LogInfo($"Reusing idle client {idleClient.GetHashCode()}");
                            MarkLeased(idleClient);
                        }
                    }

                    if (shutdown)
                    {
                        Forget(idleClient);
                        throw new ObjectDisposedException(nameof(SmtpConnectionPool));
                    }

                    return idleClient;
                }
            }

            // Konstruktor s IProtocolLogger je záměrně použitý místo nastavení
            // ProtocolLogger až po new SmtpClient() — MailKit uvnitř tohoto
            // konstruktoru sám napojí AuthenticationSecretDetector, takže hesla
            // z AUTH PLAIN/LOGIN se do session logu nikdy nedostanou v čitelné
            // podobě. Kdybychom ProtocolLogger nastavovali až přes property
            // setter, tohle automatické propojení bychom si museli hlídat sami.
            // Each SMTP connection gets its own protocol observer. A single observer
            // shared by parallel workers would mix their C:/S: streams and one worker
            // could drain another worker's pipeline events. The template is only an
            // enable/disable flag; the actual observer is per client.
            ProtocolPathObserver? clientPathObserver = _pathObserverTemplate != null
                ? new ProtocolPathObserver()
                : null;
            MailKit.IProtocolLogger? logger = null;
            if (_sessionLogger != null && clientPathObserver != null)
                logger = new CompositeProtocolLogger(new SessionProtocolLogger(_sessionLogger), clientPathObserver);
            else if (_sessionLogger != null)
                logger = new SessionProtocolLogger(_sessionLogger);
            else if (clientPathObserver != null)
                logger = clientPathObserver;

            var client = logger != null ? new SmtpClient(logger) : new SmtpClient();
            var clientOwnedByPool = false;
            try
            {
                // Ownership starts immediately after SmtpClient construction. Every
                // configuration step below can throw (proxy parsing/creation,
                // certificate collection, etc.); keeping all of it inside the same
                // cleanup boundary prevents a half-configured client from leaking.
                if (clientPathObserver != null)
                    _pathObservers[client] = clientPathObserver;
                client.Timeout = Math.Max(_options.ConnectTimeoutMs, _options.ReadTimeoutMs);
                if (_options.IgnoreCertificateErrors)
                    client.ServerCertificateValidationCallback = (_, _, _, _) => true;
                if (_clientCert != null)
                    client.ClientCertificates.Add(_clientCert);

                if (_proxyRotator != null)
                {
                    var ep = _proxyRotator.TryGetNext();
                    if (ep is null)
                        throw new InvalidOperationException("Všechny proxy jsou dočasně vyřazené (ban detection).");
                    var proxyClient = ProxyClientFactory.Create(ep);
                    if (proxyClient != null)
                    {
                        client.ProxyClient = proxyClient;
                        _clientProxy[client] = ep;
                        _sessionLogger?.LogInfo($"Proxy {ep.DisplayKey}");
                    }
                }

                _sessionLogger?.LogInfo($"Creating new connection to {_options.SmtpHost}:{_options.Port}");
                await EnsureConnectedAsync(client, ct).ConfigureAwait(false);
                _sessionLogger?.LogInfo($"Connected and authenticated");

                // The final shutdown check and lease registration must be one
            // lifecycle-critical transition. Otherwise DisposeAsync can set
            // _disposed after a check but before MarkLeased(), allowing a client
            // to escape after shutdown has begun.
                lock (_lifecycleLock)
                {
                    if (Volatile.Read(ref _disposed) != 0)
                        throw new ObjectDisposedException(nameof(SmtpConnectionPool));

                    _all.TryAdd(client, 0);
                    MarkLeased(client);
                    Interlocked.Increment(ref _created);
                    clientOwnedByPool = true;
                    return client;
                }
            }
            catch (Exception ex)
            {
                if (!clientOwnedByPool)
                {
                    _sessionLogger?.LogInfo($"Connection failed: {ex.Message}");
                    _pathObservers.TryRemove(client, out _);
                    _clientProxy.TryRemove(client, out _);
                    await SafeDisposeAsync(client).ConfigureAwait(false);
                }
                throw;
            }
        }
        catch
        {
            _gate.Release();
            throw;
        }
    }

    /// <summary>Vyzvedne pouze protocol events patřící konkrétnímu SMTP klientovi.</summary>
    public void ReportProxyBlocked(SmtpClient client)
    {
        if (_proxyRotator is null) return;
        if (_clientProxy.TryGetValue(client, out var ep))
            _proxyRotator.ReportBlocked(ep);
    }

    public ProxyEndpoint? GetClientProxy(SmtpClient client) =>
        _clientProxy.TryGetValue(client, out var ep) ? ep : null;

    public IReadOnlyList<(DeliveryStepKind Step, bool? Ok, string Detail)> DrainPathEvents(SmtpClient client)
        => _pathObservers.TryGetValue(client, out var observer)
            ? observer.Drain()
            : Array.Empty<(DeliveryStepKind, bool?, string)>();

    public void Return(SmtpClient client)
    {
        // A client owns exactly one pool permit while leased. Duplicate Return/Discard
        // calls must not inflate the semaphore count and silently exceed MaxConcurrency.
        if (!_leased.TryRemove(client, out _))
            return;

        // Shutdown must be checked atomically with the hand-off back to the idle
        // queue. A check followed by TryEnqueue is a TOCTOU race: DisposeAsync can
        // drain _idle between those two operations, after which this client would be
        // enqueued into a pool that is already disposed and never be observed again.
        bool returnToIdle;
        lock (_lifecycleLock)
        {
            returnToIdle = Volatile.Read(ref _disposed) == 0 && client.IsConnected;
            if (returnToIdle)
            {
                _idleSince[client] = Stopwatch.GetTimestamp();
                _idle.Enqueue(client);
            }
        }

        if (!returnToIdle)
        {
            Forget(client);
        }

        _gate.Release();
    }

    public void Discard(SmtpClient client)
    {
        if (!_leased.TryRemove(client, out _))
            return;
        Forget(client);
        // The permit belongs to the RentAsync call even if shutdown started.
        // Always release it so a waiter cannot remain blocked forever.
        _gate.Release();
    }

    private void MarkLeased(SmtpClient client) => _leased[client] = 0;

    void Forget(SmtpClient client)
    {
        _all.TryRemove(client, out _);
        _leased.TryRemove(client, out _);
        _idleSince.TryRemove(client, out _);
        if (_pathObservers.TryRemove(client, out var observer))
            observer.Clear();
        _clientProxy.TryRemove(client, out _);
        _ = SafeDisposeAsync(client);
    }

    async Task EnsureConnectedAsync(SmtpClient client, CancellationToken ct)
    {
        if (!client.IsConnected)
        {
            // _clientCert is attached to client.ClientCertificates (at creation time,
            // persists across reconnects) and is actively read during the TLS
            // handshake inside ConnectAsync below. The client isn't visible in
            // _leased until this whole method returns successfully, so without this
            // counter DisposeAsync could dispose _clientCert while a handshake on
            // another thread is still using it. See _inFlightConnects declaration.
            // Atomically pair the disposed-state check with entering the in-flight
            // connection window. A simple `if (_disposed == 0)` followed by an
            // Interlocked.Increment() leaves a narrow shutdown race: DisposeAsync can
            // observe zero and dispose _clientCert just before a pre-existing RentAsync
            // increments the counter and starts using that certificate.
            lock (_lifecycleLock)
            {
                if (Volatile.Read(ref _disposed) != 0)
                    throw new ObjectDisposedException(nameof(SmtpConnectionPool));
                _inFlightConnects++;
            }
            try
            {
                // Source bind priorita: IPv6 rotace → IPv4 rotace → fixní SourceIp → default
                if (_ipv6Rotator != null)
                {
                    var ip = _ipv6Rotator.GetNextRandomIp();
                    var ep = new IPEndPoint(ip, 0);
                    var socket = IpBindingHelper.CreateBoundSocket(ep, IpVersionPreference.IPv6Only);
                    await client.ConnectAsync(socket, _options.SmtpHost, _options.Port, _socket, ct).ConfigureAwait(false);
                }
                else if (_ipv4Rotator != null)
                {
                    var ip = _ipv4Random ? _ipv4Rotator.GetRandomIp() : _ipv4Rotator.GetNextIp();
                    var ep = new IPEndPoint(ip, 0);
                    var socket = IpBindingHelper.CreateBoundSocket(ep, IpVersionPreference.IPv4Only);
                    await client.ConnectAsync(socket, _options.SmtpHost, _options.Port, _socket, ct).ConfigureAwait(false);
                }
                else if (_localEp != null)
                {
                    var socket = IpBindingHelper.CreateBoundSocket(_localEp, _options.IpVersion);
                    await client.ConnectAsync(socket, _options.SmtpHost, _options.Port, _socket, ct).ConfigureAwait(false);
                }
                else
                {
                    await client.ConnectAsync(_options.SmtpHost, _options.Port, _socket, ct).ConfigureAwait(false);
                }

                if (_options.UseAuthentication)
                {
                    if (_options.AuthMethod == SmtpAuthMethod.Auto)
                    {
                        await client.AuthenticateAsync(_options.Username, _options.Password, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        var sasl = AuthMethodHelper.CreateSasl(_options.AuthMethod, _options.Username, _options.Password);
                        await client.AuthenticateAsync(sasl, ct).ConfigureAwait(false);
                    }
                    _sessionLogger?.LogInfo("Authentication succeeded");
                }
            }
            finally
            {
                lock (_lifecycleLock)
                {
                    _inFlightConnects--;
                }
            }
        }
        else if (_options.UseAuthentication && !client.IsAuthenticated)
        {
            if (_options.AuthMethod == SmtpAuthMethod.Auto)
                await client.AuthenticateAsync(_options.Username, _options.Password, ct).ConfigureAwait(false);
            else
            {
                var sasl = AuthMethodHelper.CreateSasl(_options.AuthMethod, _options.Username, _options.Password);
                await client.AuthenticateAsync(sasl, ct).ConfigureAwait(false);
            }
        }
    }

    static async Task SafeDisposeAsync(SmtpClient client)
    {
        try { if (client.IsConnected) await client.DisconnectAsync(false).ConfigureAwait(false); }
        catch { }
        try { client.Dispose(); }
        catch { }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_lifecycleLock)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            Volatile.Write(ref _disposed, 1);
        }

        while (_idle.TryDequeue(out var c))
        {
            _all.TryRemove(c, out _);
            _leased.TryRemove(c, out _);
            _idleSince.TryRemove(c, out _);
            if (_pathObservers.TryRemove(c, out var observer)) observer.Clear();
            await SafeDisposeAsync(c).ConfigureAwait(false);
        }

        // Do not enumerate/dispose _all here: _all also contains leased clients.
        // A leased client is owned by a worker until Return/Discard. Removing it
        // from _leased during shutdown would make the later Return a no-op and
        // leak the semaphore permit. Once shutdown starts, Return/Discard will
        // dispose the client and release exactly its original permit.
        //
        // The runner normally awaits all workers before disposing the pool, but
        // keeping this invariant inside the pool makes the class safe against
        // accidental concurrent shutdown as well.

        // Do not dispose SemaphoreSlim while RentAsync callers may still be
        // waiting on it. Active renters release their permit through Return /
        // Discard, at which point a waiter wakes, observes _disposed and exits.

        // _clientCert wraps a native crypto handle (and, without EphemeralKeySet,
        // could persist private-key material to disk). It is also read by MailKit
        // during the TLS handshake. Never dispose it while a handshake is still
        // inside EnsureConnectedAsync: a timeout in this wait would turn a
        // resource-leak protection into a use-after-dispose race.
        //
        // The runner cancels active workers before reaching pool disposal, so a
        // normal shutdown should release this counter promptly. We deliberately
        // wait for the in-flight window instead of using a hard timeout: disposing
        // the certificate is a safety boundary, and it is better for DisposeAsync
        // to remain pending than to invalidate a certificate still used by TLS.
        while (Volatile.Read(ref _inFlightConnects) > 0)
            await Task.Delay(10).ConfigureAwait(false);

        _clientCert?.Dispose();
    }

    public int CreatedCount => Volatile.Read(ref _created);
}
