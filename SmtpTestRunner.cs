using System.Collections.Concurrent;
using System.Diagnostics;
using MailKit.Net.Smtp;
using MimeKit;

namespace MailLoadTester;

public sealed class SmtpTestRunner
{
    /// <summary>
    /// Spustí test. Pokud je zapnutý AutoRestartOnFailure, celý test se po
    /// "většinovém selhání" (víc zpráv selhalo než uspělo, a nebylo to STOPnuté
    /// uživatelem) automaticky zopakuje s napůl sníženým paralelismem, max.
    /// AutoRestartMaxAttempts-krát. Vrací výsledek posledního pokusu.
    /// </summary>
    public async Task<MailTestResult> RunAsync(
        MailTestOptions options,
        IProgress<ProgressUpdate> progress,
        CancellationToken ct)
    {
        if (!options.AutoRestartOnFailure)
            return await RunSingleAsync(options, progress, ct, attemptIndex: 0).ConfigureAwait(false);

        var current = options;
        MailTestResult result;
        int attempt = 0;
        // Aggregate across restarts — last-run-only under-reports Sent/throughput.
        var sumSent = 0;
        var sumFailed = 0;
        var sumRetries = 0;
        var sumSmtp4xx = 0;
        var sumSmtp5xx = 0;
        var sumTimeouts = 0;
        var totalElapsed = TimeSpan.Zero;
        var lastError = "";

        while (true)
        {
            result = await RunSingleAsync(current, progress, ct, attemptIndex: attempt).ConfigureAwait(false);
            attempt++;

            sumSent += result.Sent;
            sumFailed += result.Failed;
            sumRetries += result.Retries;
            sumSmtp4xx += result.Smtp4xx;
            sumSmtp5xx += result.Smtp5xx;
            sumTimeouts += result.Timeouts;
            totalElapsed += result.Elapsed;
            if (!string.IsNullOrEmpty(result.LastError))
                lastError = result.LastError;

            if (result.Cancelled) break;

            var mostlyFailed = result.Requested > 0 && result.Failed > result.Sent;
            if (!mostlyFailed || attempt > options.AutoRestartMaxAttempts) break;

            var newConcurrency = Math.Max(1, current.MaxConcurrency / 2);
            progress.Report(new ProgressUpdate(sumSent, sumFailed,
                $"Auto-restart {attempt}/{options.AutoRestartMaxAttempts}: poslední běh {result.Failed}/{result.Requested} selhalo — " +
                $"snižuji paralelismus {current.MaxConcurrency}→{newConcurrency} a zkouším znovu za 3 s…",
                null, 0, "Auto-restart", ""));
            current = current with { MaxConcurrency = newConcurrency };

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                result = result with { Cancelled = true };
                break;
            }
        }

        var elapsedSec = totalElapsed.TotalSeconds;
        return result with
        {
            Sent = sumSent,
            Failed = sumFailed,
            Retries = sumRetries,
            Smtp4xx = sumSmtp4xx,
            Smtp5xx = sumSmtp5xx,
            Timeouts = sumTimeouts,
            Elapsed = totalElapsed,
            LastError = lastError,
            ThroughputPerSec = elapsedSec > 0 ? sumSent / elapsedSec : 0,
            AutoRestartAttempts = Math.Max(0, attempt - 1)
        };
    }

    async Task<MailTestResult> RunSingleAsync(
        MailTestOptions options,
        IProgress<ProgressUpdate> progress,
        CancellationToken ct,
        int attemptIndex)
    {
        var fsm = new TestStateMachine();
        var pace = new SmartPaceController(options);
        var observed = options.CollectObservedResponses ? new ObservedResponseCollector() : null;
        var pathObserver = new ProtocolPathObserver(); // template: pool creates one observer per connection

        // Declared before Report() so the local function below can safely reference it
        // at every call site (a local function may only read a captured local once it
        // is definitely assigned at the *call* site, and Report() is first invoked
        // further down before the old declaration point).
        var dashboard = options.EnableDashboard ? new DashboardServer(options.DashboardPort) : null;
        string? dashboardStartupWarning = null;
        if (dashboard != null)
        {
            try { dashboard.Start(); }
            catch (Exception ex)
            {
                // A busy port must not abort the whole test run — the dashboard
                // is a convenience feature, not a prerequisite for sending mail.
                dashboardStartupWarning = $"Dashboard se nepodařilo spustit na portu {options.DashboardPort}: {ex.Message}";
                dashboard.Dispose();
                dashboard = null;
            }
        }
        var dashboardSw = Stopwatch.StartNew();

        void UpdateDashboard(int s, int f, string status, double? eta, string phase)
        {
            if (dashboard is null) return;
            var requested = options.MessageCount;
            var done = s + f;
            var pct = requested > 0 ? Math.Round(100.0 * done / requested, 1) : 0.0;
            var elapsedSeconds = dashboardSw.Elapsed.TotalSeconds;
            var tput = elapsedSeconds > 0 ? s / elapsedSeconds : 0.0;
            dashboard.Update(new DashboardState(s, f, requested, pct, tput, eta, phase, status));
        }

        // Throttle progress: UI/IProgress is a major bottleneck at high concurrency.
        // Always report: phases 0–2 (setup), terminal message steps, path failures.
        // Phase 3 routine status: at most ~8/s + every 25 successful sends.
        var reportGate = new object();
        var lastReportTicks = 0L;
        var suppressedReports = 0;
        const int MinReportIntervalMs = 125;

        void Report(int s, int f, string status, double? eta, int phase, string current, string next,
            MessageStep? step = null, int? messageIndex = null, int? workerId = null,
            DeliveryStepKind? pathStep = null, bool? pathOk = null,
            bool includeObserved = false)
        {
            var terminal = step is MessageStep.Succeeded or MessageStep.FailedFinal or MessageStep.FailedTransient;
            var setup = phase <= 2;
            var pathFail = pathOk == false;
            var milestone = terminal && s > 0 && s % 25 == 0;

            if (!setup && !terminal && !pathFail && !includeObserved)
            {
                var now = Environment.TickCount64;
                lock (reportGate)
                {
                    if (now - lastReportTicks < MinReportIntervalMs)
                    {
                        suppressedReports++;
                        return;
                    }
                    lastReportTicks = now;
                }
            }
            else if (terminal || setup || pathFail)
            {
                lock (reportGate) lastReportTicks = Environment.TickCount64;
            }

            progress.Report(new ProgressUpdate(s, f, status, eta, phase, current, next,
                fsm.Phase, step, messageIndex, workerId, options.MaxConcurrency,
                pathStep, pathOk, pace.StatusText, pace.CurrentIntervalMs,
                includeObserved ? observed?.Snapshot() : null));

            UpdateDashboard(s, f, status, eta, fsm.Phase.ToString());
        }

        void ReportPathEvents(int s, int f, int? i, int? workerId, SmtpClient client)
        {
            // Path: only publish failure or final success colors, not every intermediate C:/S: line under load
            foreach (var (step, ok, detail) in pool!.DrainPathEvents(client))
            {
                if (ok == true && step is not (DeliveryStepKind.Data or DeliveryStepKind.Quit or DeliveryStepKind.Error))
                {
                    // throttle successful intermediate path steps
                    var now = Environment.TickCount64;
                    lock (reportGate)
                    {
                        if (now - lastReportTicks < MinReportIntervalMs)
                        {
                            suppressedReports++;
                            continue;
                        }
                        lastReportTicks = now;
                    }
                }
                progress.Report(new ProgressUpdate(s, f,
                    detail, null, 3, detail, "",
                    fsm.Phase, null, i, workerId, options.MaxConcurrency,
                    step, ok, pace.StatusText, pace.CurrentIntervalMs, null));
            }
        }

        if (dashboardStartupWarning != null)
            Report(0, 0, dashboardStartupWarning, null, 0, dashboardStartupWarning, "");

        fsm.Transition(TestPhase.Validating, "Validating options");
        try { Validation.Validate(options); }
        catch { fsm.ForceFailure("Validation failed"); throw; }

        // Load EML template only after validation and with hard file/attachment quotas.
        // This prevents a large embedded MIME attachment from being decoded into an
        // unbounded byte[] before AttachmentPlanner/RAM safety gets involved.
        string? emlSubject = null, emlBody = null;
        bool emlIsHtml = false;
        IReadOnlyList<(string FileName, byte[] Content)> emlAttachments = Array.Empty<(string, byte[])>();
        if (!string.IsNullOrEmpty(options.EmlTemplatePath))
        {
            var availableForEml = AttachmentPlanner.GetAvailableMemoryBytes();
            var emlAttachmentBudget = Math.Min(AttachmentPlanner.DefaultMaxAttachmentBytes,
                Math.Max(8L * 1024 * 1024, availableForEml / 10));
            emlAttachmentBudget = Math.Min(emlAttachmentBudget, Math.Max(1L, availableForEml));
            var eml = EmlTemplateParser.Parse(options.EmlTemplatePath,
                EmlTemplateParser.DefaultMaxFileBytes, emlAttachmentBudget);
            emlSubject = eml.Subject;
            emlBody = eml.Body;
            emlIsHtml = eml.IsHtml;
            emlAttachments = eml.Attachments;
        }

        // Direct MX delivery
        IReadOnlyList<MxRecord>? mxRecords = null;
        if (options.DirectMxDelivery)
        {
            Report(0, 0, "Resolving MX records…", null, 1, "DNS MX lookup", "SMTP spojení",
                pathStep: DeliveryStepKind.DnsMxLookup, pathOk: null);
            mxRecords = await MxResolver.ResolveAsync(options.Recipients[0].Split('@').Last(), ct).ConfigureAwait(false);
            var bestMx = MxResolver.GetBestHost(mxRecords);
            options = options with { SmtpHost = bestMx };
            Report(0, 0, $"MX: {bestMx}", null, 1, $"MX resolved: {bestMx}", "SMTP spojení",
                pathStep: DeliveryStepKind.DnsMxLookup, pathOk: true);
        }
        else
        {
            // Relay režim – DNS MX se neprovádí, ale krok označíme jako přeskočený/úspěšný konceptuálně (host je zadaný)
            Report(0, 0, $"SMTP relay: {options.SmtpHost}", null, 1, "Relay host", "SMTP spojení",
                pathStep: DeliveryStepKind.DnsMxLookup, pathOk: true);
        }

        // Session logger
        SmtpSessionLogger? sessionLogger = null;
        if (options.EnableSessionLog && !string.IsNullOrEmpty(options.SessionLogPath))
        {
            sessionLogger = new SmtpSessionLogger(options.SessionLogPath, append: attemptIndex > 0);
        }

        var sw = Stopwatch.StartNew();
        long activeTicks = 0;
        int sent = 0, failed = 0;
        int retries = 0, smtp4xx = 0, smtp5xx = 0, timeouts = 0;
        string lastError = "";
        var latencies = new ConcurrentBag<double>();
        var startTime = DateTime.UtcNow;
        // IntervalMs = GLOBÁLNÍ spacing ve SmartPaceController (sdílená fronta slotů pro všechny workery).
        // RateLimiter zde nepřidává druhé čekání.
        var rateLimiter = new RateLimiter(0);
        bool cancelled = false;
        var adaptive = options.UseAdaptiveConcurrency
            ? new AdaptiveConcurrencyLimiter(options.MaxConcurrency, min: 1, max: options.MaxConcurrency)
            : null;
        var circuit = options.UseCircuitBreaker
            ? new CircuitBreaker(
                options.CircuitBreakerThreshold,
                windowSize: options.CircuitBreakerWindowSize,
                failureRatePercent: options.CircuitBreakerFailurePercent)
            : null;
        var bandwidth = options.BandwidthLimitKbps > 0 ? new BandwidthLimiter(options.BandwidthLimitKbps) : null;
        MimeKit.FormatOptions? utf8Format = null;
        if (options.SmtpUtf8)
        {
            utf8Format = MimeKit.FormatOptions.Default.Clone();
            utf8Format.International = true;
        }

        var randomAttachmentSizeMb = 0;
        if (options.GenerateRandomAttachments)
        {
            var safety = AttachmentPlanner.EstimateRandomAttachments(options.RandomAttachmentSizeMb, options.MaxRandomAttachments, options.MaxConcurrency);
            if (!safety.IsSafe)
                throw new InvalidOperationException("Náhodné přílohy byly z bezpečnostních důvodů odmítnuty před alokací RAM. " + safety.Explanation);
            randomAttachmentSizeMb = AttachmentPlanner.ResolveRandomAttachmentSizeMb(options.RandomAttachmentSizeMb, options.MaxRandomAttachments, options.MaxConcurrency);
            Report(0, 0, "ATTACHMENT SAFETY", null, 1, safety.Explanation + $" Použitá velikost: {randomAttachmentSizeMb} MB/příloha.", "Příprava příloh");
        }

        fsm.Transition(TestPhase.PreparingAttachments, "Attachment planner");
        Report(0, 0, "Příprava příloh…", null, 2,
            "Připravuji přílohy a plán paměti",
            options.DryRun ? "Simulace odesílání (dry-run)" : "Vytvoření SMTP spojení / poolu");

        // Inline (CID) attachments are small, reused verbatim for every message, and
        // were previously re-opened via File.OpenRead() once per message per worker —
        // under high MaxConcurrency and a large MessageCount that meant many
        // concurrently open file handles for the same handful of files. Preload once.
        var inlinePaths = (options.InlineAttachments ?? Array.Empty<string>())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        long inlineTotalBytes = 0;
        foreach (var inlinePath in inlinePaths)
        {
            var info = new FileInfo(inlinePath);
            if (!info.Exists)
                throw new FileNotFoundException($"Inline příloha neexistuje: {inlinePath}", inlinePath);
            inlineTotalBytes = checked(inlineTotalBytes + info.Length);
        }
        if (inlineTotalBytes > 0)
            AttachmentPlanner.EnsurePreloadSafe(inlineTotalBytes, "inline/CID přílohy");

        var inlineAttachmentData = new List<(string FileName, string Extension, byte[] Content)>();
        foreach (var inlinePath in inlinePaths)
        {
            inlineAttachmentData.Add((
                Path.GetFileName(inlinePath),
                Path.GetExtension(inlinePath).TrimStart('.').ToLowerInvariant(),
                File.ReadAllBytes(inlinePath)));
        }

        var attachmentPlan = AttachmentPlanner.Prepare(options.Attachments, options.MaxConcurrency);
        if (emlAttachments.Count > 0)
        {
            // Přílohy z EML šablony přidáme k naplánovaným přílohám (jsou už v paměti,
            // takže je prostě přidáme jako přednačtené zdroje).
            var merged = attachmentPlan.Sources.ToList();
            foreach (var (fileName, content) in emlAttachments)
                merged.Add(new AttachmentSource(fileName, "", content, content.LongLength));
            attachmentPlan = attachmentPlan with { Sources = merged };
        }
        if (attachmentPlan.Sources.Count > 0)
            Report(0, 0, $"Přílohy: {attachmentPlan.Description}", null, 1,
                "Přílohy připraveny: " + (attachmentPlan.Preloaded ? "v RAM" : "z disku"),
                options.DryRun ? "Simulace odesílání" : "SMTP spojení");

        fsm.Transition(options.DryRun ? TestPhase.Sending : TestPhase.ConnectingSmtp,
            options.DryRun ? "Dry-run" : "Create SMTP pool");
        Report(0, 0, options.DryRun ? "Dry-run (bez SMTP)" : "Připojuji SMTP pool…", null, 2,
            options.DryRun ? "Dry-run režim — bez reálného serveru" : "Navazuji SMTP spojení (pool)",
            "Odesílání zpráv" + (options.MaxConcurrency > 1 ? $" (paralelismus {options.MaxConcurrency})" : ""));

        SmtpConnectionPool? pool = null;

        try
        {
            if (!options.DryRun)
            {
                pool = new SmtpConnectionPool(options, sessionLogger, pathObserver);
                if (options.PreWarmConnections)
                {
                    Report(0, 0, "Pre-warming SMTP connections…", null, 2, "Pre-warm spojení", "Odesílání");
                    await pool.PreWarmAsync(options.MaxConcurrency, ct).ConfigureAwait(false);
                    Report(0, 0, $"Pre-warmed {pool.CreatedCount} connections", null, 2, "Spojení připravena", "Odesílání");
                }
            }

            for (int batchStart = 1; batchStart <= options.MessageCount; batchStart += options.BatchMode ? options.BatchSize : options.MessageCount)
            {
                ct.ThrowIfCancellationRequested();
                int batchEnd = options.BatchMode
                    ? Math.Min(options.MessageCount, batchStart + options.BatchSize - 1)
                    : options.MessageCount;

                var batchActiveSw = Stopwatch.StartNew();
                if (fsm.Phase == TestPhase.ConnectingSmtp)
                    fsm.Transition(TestPhase.Sending, "First batch");

                var tasks = Enumerable.Range(batchStart, batchEnd - batchStart + 1).Select(async i =>
                {
                    var workerId = ((i - batchStart) % Math.Max(1, options.MaxConcurrency)) + 1;
                    Report(Volatile.Read(ref sent), Volatile.Read(ref failed), $"QUEUE #{i} · W{workerId}", null, 3,
                        $"Zpráva #{i} ve frontě · W{workerId}", "Rate limit",
                        MessageStep.Queued, i, workerId);

                    // Tempo a ochrana: jitter, burst, backoff, time window, greylist, per-recipient
                    await pace.WaitBeforeSendAsync(
                        options.Recipients[(i - 1) % options.Recipients.Count], ct).ConfigureAwait(false);

                    // WaitBeforeSendAsync už rezervoval per-recipient slot (pokud je limit zapnutý).
                    // RateLimiter/AdaptiveConcurrency mohou být zrušeny ještě před vstupem do
                    // hlavního try/finally níže; v tom případě musíme rezervaci příjemce vrátit,
                    // jinak by při STOP/cancel zůstala jako phantom reservation až do konce okna.
                    var recipient = options.Recipients[(i - 1) % options.Recipients.Count];
                    var recipientCommitted = false;
                    var adaptiveAcquired = false;
                    try
                    {
                        await rateLimiter.WaitAsync(ct).ConfigureAwait(false);

                        if (adaptive != null)
                        {
                            await adaptive.AcquireAsync(ct).ConfigureAwait(false);
                            adaptiveAcquired = true;
                        }
                    }
                    catch
                    {
                        pace.ReleaseRecipient(recipient);
                        throw;
                    }

                    try
                    {
                    if (options.GenerateRandomAttachments)
                    {
                        // The initial preflight can become stale if another application
                        // consumes RAM during a long test. Re-check immediately before
                        // generating each message's large random payload.
                        var runtimeSafety = AttachmentPlanner.EstimateRandomAttachments(
                            randomAttachmentSizeMb, options.MaxRandomAttachments, options.MaxConcurrency);
                        if (!runtimeSafety.IsSafe)
                            throw new InvalidOperationException(
                                "RAM safety re-check rejected random attachment generation before allocation. " +
                                runtimeSafety.Explanation);
                    }

                    var data = options.RandomTestData || options.UseBogusData ||
                               options.GenerateRandomHtml || options.GenerateRandomAttachments ||
                               options.VarySubjectBodyPerMessage
                        ? RandomTestData.CreateContent(
                            i,
                            options.UseBogusData,
                            options.GenerateRandomHtml,
                            options.GenerateRandomAttachments,
                            options.MaxRandomAttachments,
                            options.VarySubjectBodyPerMessage,
                            randomAttachmentSizeMb)
                        : new RandomTestData.GeneratedMessageContent(
                            options.DisplayName,
                            emlSubject ?? options.Subject,
                            emlBody ?? options.Body,
                            emlIsHtml || options.HtmlBody,
                            Array.Empty<RandomTestData.GeneratedAttachment>());
                    var msgSw = Stopwatch.StartNew();
                    Exception? lastEx = null;
                    bool success = false;
                    bool retryable = false;

                    for (int attempt = 0; attempt <= options.MaxRetries; attempt++)
                    {
                        ct.ThrowIfCancellationRequested();

                        // Fail-fast i pro ÚPLNĚ NOVÉ zprávy (ne jen retry téže zprávy) —
                        // jinak jistič nezastaví čerstvé pokusy během výpadku serveru.
                        if (circuit != null && circuit.IsAnyOpen(out var openCategory))
                        {
                            lastEx = new InvalidOperationException($"Circuit breaker OPEN ({openCategory}) — čekám na cooldown.");
                            break;
                        }
                        if (circuit != null && lastEx != null && circuit.IsOpen(lastEx))
                        {
                            lastEx = new InvalidOperationException("Circuit breaker OPEN — too many consecutive failures.");
                            break;
                        }

                        SmtpClient? client = null;
                        try
                        {
                            if (options.DryRun)
                            {
                                // Dry-run: simulujeme úspěšnou cestu bez reálného SMTP
                                Report(Volatile.Read(ref sent), Volatile.Read(ref failed), $"DRY #{i}", null, 3,
                                    "Dry-run TCP", "MIME", MessageStep.RentingConnection, i, workerId,
                                    DeliveryStepKind.TcpConnect, true);
                                Report(Volatile.Read(ref sent), Volatile.Read(ref failed), $"DRY #{i}", null, 3,
                                    "Dry-run EHLO/TLS/AUTH", "SEND", MessageStep.BuildingMime, i, workerId,
                                    DeliveryStepKind.Ehlo, true);
                                Report(Volatile.Read(ref sent), Volatile.Read(ref failed), $"DRY #{i}", null, 3,
                                    "Dry-run STARTTLS", "AUTH", null, i, workerId,
                                    DeliveryStepKind.StartTls, true);
                                Report(Volatile.Read(ref sent), Volatile.Read(ref failed), $"DRY #{i}", null, 3,
                                    "Dry-run AUTH", "MAIL FROM", null, i, workerId,
                                    DeliveryStepKind.Auth, true);
                                Report(Volatile.Read(ref sent), Volatile.Read(ref failed), $"DRY #{i}", null, 3,
                                    "Dry-run MAIL FROM", "RCPT TO", null, i, workerId,
                                    DeliveryStepKind.MailFrom, true);
                                Report(Volatile.Read(ref sent), Volatile.Read(ref failed), $"DRY #{i}", null, 3,
                                    "Dry-run RCPT TO", "DATA", null, i, workerId,
                                    DeliveryStepKind.RcptTo, true);
                                await Task.Delay(Random.Shared.Next(15, 80), ct).ConfigureAwait(false);
                                Report(Volatile.Read(ref sent), Volatile.Read(ref failed), $"DRY #{i}", null, 3,
                                    "Dry-run DATA", "QUIT", MessageStep.SmtpSend, i, workerId,
                                    DeliveryStepKind.Data, true);
                            }
                            else
                            {
                                // TCP + handshake probíhá uvnitř pool.RentAsync (connect/EHLO/STARTTLS/AUTH)
                                Report(Volatile.Read(ref sent), Volatile.Read(ref failed), $"CONNECT #{i} · W{workerId}", null, 3,
                                    $"SMTP spojení · zpráva #{i} · W{workerId}", "MIME zprávy",
                                    MessageStep.RentingConnection, i, workerId,
                                    DeliveryStepKind.TcpConnect, null);
                                client = await pool!.RentAsync(ct).ConfigureAwait(false);
                                ReportPathEvents(Volatile.Read(ref sent), Volatile.Read(ref failed), i, workerId, client);
                                // Po úspěšném Rent: TCP + EHLO (+ STARTTLS + AUTH podle konfigurace) proběhly
                                // TCP/EHLO/STARTTLS/AUTH barvy z ProtocolPathObserver (skutečné C:/S: řádky)
                                Report(Volatile.Read(ref sent), Volatile.Read(ref failed), $"MIME #{i} · W{workerId}", null, 3,
                                    $"Stavím MIME · zpráva #{i} · W{workerId}", "SMTP SEND",
                                    MessageStep.BuildingMime, i, workerId);
                                var messageAttachments = new List<AttachmentSource>(attachmentPlan.Sources);
                                foreach (var generated in data.Attachments)
                                    messageAttachments.Add(new AttachmentSource(
                                        generated.FileName, "", generated.Content, generated.Content.LongLength));

                                using var message = BuildMessage(
                                    options, data, recipient, messageAttachments, inlineAttachmentData, i,
                                    data.IsHtml || emlIsHtml || options.HtmlBody);
                                if (bandwidth != null)
                                {
                                    var sizeCounter = new CountingStream();
                                    message.WriteTo(sizeCounter);
                                    await bandwidth.ThrottleAsync((int)Math.Min(int.MaxValue, sizeCounter.BytesWritten), ct).ConfigureAwait(false);
                                }
                                Report(Volatile.Read(ref sent), Volatile.Read(ref failed), $"SEND #{i} · W{workerId}", null, 3,
                                    $"SMTP SEND · zpráva #{i} · W{workerId}", "OK nebo retry",
                                    MessageStep.SmtpSend, i, workerId);
                                if (utf8Format != null)
                                    await client.SendAsync(utf8Format, message, ct).ConfigureAwait(false);
                                else
                                    await client.SendAsync(message, ct).ConfigureAwait(false);
                                ReportPathEvents(Volatile.Read(ref sent), Volatile.Read(ref failed), i, workerId, client);
                                pool.Return(client);
                                client = null;
                            }
                            success = true;
                            circuit?.RecordSuccess();
                            adaptive?.RecordAttempt(true);
                            pace.CommitRecipient(recipient);
                            recipientCommitted = true;
                            pace.RecordSuccess(recipient);
                            observed?.Record(250, "2.0.0 OK");
                            break;
                        }
                        catch (OperationCanceledException)
                        {
                            if (client is not null) pool?.Discard(client);
                            throw;
                        }
                        catch (Exception ex) when (IsTransient(ex) && attempt < options.MaxRetries)
                        {
                            if (client is not null)
                            {
                                if (IpBanDetector.IsLikelyIpOrProxyBan(ex.Message))
                                    pool!.ReportProxyBlocked(client);
                                pool!.Discard(client);
                                client = null;
                            }
                            lastEx = ex;
                            retryable = true;
                            Interlocked.Increment(ref retries);
                            circuit?.RecordFailure(ex);
                            adaptive?.RecordAttempt(false);
                            if (ex is SmtpCommandException sce)
                            {
                                var code = (int)sce.StatusCode;
                                if (code >= 400 && code < 500) Interlocked.Increment(ref smtp4xx);
                                observed?.Record(code, sce.Message);
                                if (ObservedResponseCollector.IsGreylist(code, sce.Message))
                                    pace.RecordGreylist();
                                else
                                    pace.RecordTransientThrottle();
                            }
                            else if (ex is TimeoutException)
                            {
                                Interlocked.Increment(ref timeouts);
                            }
                            Report(Volatile.Read(ref sent), Volatile.Read(ref failed),
                                $"RETRY #{i} · W{workerId}", null, 3,
                                $"Dočasná chyba · zpráva #{i} · W{workerId}",
                                $"Retry {attempt + 1}/{options.MaxRetries}",
                                MessageStep.FailedTransient, i, workerId,
                                DeliveryStepKind.Error, false);
                            await Task.Delay(GetRetryDelay(attempt), ct).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            if (client is not null)
                            {
                                if (IpBanDetector.IsLikelyIpOrProxyBan(ex.Message))
                                    pool!.ReportProxyBlocked(client);
                                pool!.Discard(client);
                                client = null;
                            }
                            lastEx = ex;
                            circuit?.RecordFailure(ex);
                            adaptive?.RecordAttempt(false);
                            if (ex is SmtpCommandException sce)
                            {
                                var code = (int)sce.StatusCode;
                                if (code >= 400 && code < 500) Interlocked.Increment(ref smtp4xx);
                                else if (code >= 500) Interlocked.Increment(ref smtp5xx);
                                observed?.Record(code, sce.Message);
                                if (ObservedResponseCollector.IsGreylist(code, sce.Message))
                                    pace.RecordGreylist();
                                else if (code >= 400 && code < 500)
                                    pace.RecordTransientThrottle();
                            }
                            else if (ex is TimeoutException)
                            {
                                Interlocked.Increment(ref timeouts);
                            }
                            break;
                        }
                    }

                    msgSw.Stop();
                    var explained = lastEx != null ? Validation.ExplainSmtpError(lastEx) : "";
                    if (success)
                    {
                        var s = Interlocked.Increment(ref sent);
                        latencies.Add(msgSw.Elapsed.TotalMilliseconds);
                        var done = s + Volatile.Read(ref failed);
                        var eta = EstimateEta(done, options.MessageCount, startTime, sw);
                        var label = options.DryRun ? "DRY-RUN OK" : "OK";
                        var remain = options.MessageCount - done;
                        // Snapshot observed každých 10 OK nebo vždy při chybě (níže)
                        Report(s, Volatile.Read(ref failed),
                            $"{label} #{i} → {recipient} ({msgSw.ElapsedMilliseconds} ms)", eta, 3,
                            $"Hotovo #{i} → {recipient} ({msgSw.ElapsedMilliseconds} ms) · celkem OK {s}",
                            remain > 0
                                ? $"Zbývá odeslat ~{remain} zpráv" + (options.MaxConcurrency > 1 ? $" (až {options.MaxConcurrency} najednou)" : "")
                                : "Souhrn a statistiky",
                            MessageStep.Succeeded, i, workerId,
                            DeliveryStepKind.Quit, true,
                            includeObserved: s % 10 == 0 || remain == 0);
                    }
                    else
                    {
                        var f = Interlocked.Increment(ref failed);
                        Interlocked.Exchange(ref lastError, explained);
                        var done = Volatile.Read(ref sent) + f;
                        var eta = EstimateEta(done, options.MessageCount, startTime, sw);
                        var retryInfo = retryable ? " (po retry)" : "";
                        Report(Volatile.Read(ref sent), f,
                            $"FAIL #{i}{retryInfo}: {explained}", eta, 3,
                            $"Zastaveno na zprávě #{i}: {explained}",
                            done < options.MessageCount ? "Pokračuji dalšími zprávami (nebo STOP)" : "Souhrn chyb",
                            MessageStep.FailedFinal, i, workerId,
                            DeliveryStepKind.Error, false,
                            includeObserved: true);
                    }
                    }
                    finally
                    {
                        if (!recipientCommitted)
                            pace.ReleaseRecipient(recipient);
                        if (adaptiveAcquired) adaptive!.Release();
                    }
                }).ToArray();

                try
                {
                    await Task.WhenAll(tasks).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                }
                if (cancelled)
                {
                    fsm.Transition(TestPhase.Cancelled, "Cancellation during batch");
                    foreach (var t in tasks)
                    {
                        try { await t.ConfigureAwait(false); }
                        catch { }
                    }
                    batchActiveSw.Stop();
                    Interlocked.Add(ref activeTicks, batchActiveSw.ElapsedTicks);
                    break;
                }

                batchActiveSw.Stop();
                Interlocked.Add(ref activeTicks, batchActiveSw.ElapsedTicks);

                if (options.BatchMode && batchEnd < options.MessageCount)
                {
                    fsm.Transition(TestPhase.BatchPause, "Batch completed");
                    Report(sent, failed,
                        $"PAUZA — dávka {batchEnd - batchStart + 1} dokončena, čekám {options.BatchPauseSeconds}s…", null, 3,
                        $"Dávka dokončena ({batchEnd}/{options.MessageCount})",
                        $"Za {options.BatchPauseSeconds}s začne další dávka");
                    await Task.Delay(TimeSpan.FromSeconds(options.BatchPauseSeconds), ct).ConfigureAwait(false);
                    fsm.Transition(TestPhase.Sending, "Next batch");
                }
            }
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
            fsm.Transition(TestPhase.Cancelled, "User cancellation");
        }
        finally
        {
            sw.Stop();
            if (pool != null)
                await pool.DisposeAsync().ConfigureAwait(false);
            sessionLogger?.Dispose();
            dashboard?.Dispose();
        }

        if (!cancelled && fsm.Phase == TestPhase.Sending)
            fsm.Transition(TestPhase.Completed, "All batches completed");

        var latencyArr = latencies.OrderBy(x => x).ToArray();
        double avg = latencyArr.Length > 0 ? latencyArr.Average() : 0;
        double min = latencyArr.Length > 0 ? latencyArr[0] : 0;
        double max = latencyArr.Length > 0 ? latencyArr[^1] : 0;
        double p50 = Percentile(latencyArr, 0.50);
        double p95 = Percentile(latencyArr, 0.95);
        double p99 = Percentile(latencyArr, 0.99);
        double throughput = sw.Elapsed.TotalSeconds > 0 ? sent / sw.Elapsed.TotalSeconds : 0;
        double activeSeconds = activeTicks / (double)Stopwatch.Frequency;
        double activeThroughput = activeSeconds > 0 ? sent / activeSeconds : 0;

        if (cancelled && string.IsNullOrEmpty(lastError))
            lastError = "Zastaveno uživatelem";

        var poolConnections = pool?.CreatedCount ?? 0;
        var adaptiveConcurrency = adaptive?.Current ?? options.MaxConcurrency;
        var circuitOpen = circuit?.EverOpened ?? false;

        return new MailTestResult(options.MessageCount, sent, failed, sw.Elapsed, lastError,
            avg, min, max, p50, p95, p99, throughput, cancelled, activeThroughput, retries, smtp4xx, smtp5xx, timeouts, poolConnections,
            adaptiveConcurrency, circuitOpen, mxRecords?.Select(r => r.Host).ToList() ?? Array.Empty<string>());
    }

    static MimeMessage BuildMessage(
        MailTestOptions options,
        RandomTestData.GeneratedMessageContent data,
        string recipient,
        IReadOnlyList<AttachmentSource> attachments,
        IReadOnlyList<(string FileName, string Extension, byte[] Content)> inlineAttachments,
        int testId,
        bool isHtml)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(data.Name, options.From));
        message.To.Add(MailboxAddress.Parse(recipient));

        // BCC & CC
        foreach (var cc in options.CcRecipients ?? Array.Empty<string>())
            message.Cc.Add(MailboxAddress.Parse(cc));
        foreach (var bcc in options.BccRecipients ?? Array.Empty<string>())
            message.Bcc.Add(MailboxAddress.Parse(bcc));

        // Dynamické tagy {TIMESTAMP}, {GUID}, {RANDOM_WORD[:N]}, {TEST_ID}
        message.Subject = TemplateTags.Process(data.Subject, testId);
        message.Headers["X-MailLoadTester-Test-ID"] = testId.ToString(System.Globalization.CultureInfo.InvariantCulture);

        // SMTPUTF8 is an SMTP transport option, not a MIME message header.
        // The runner enables MimeKit FormatOptions.International when requested.

        var processedBody = TemplateTags.Process(data.Body, testId);
        var builder = new BodyBuilder();
        if (isHtml)
            builder.HtmlBody = processedBody;
        else
            builder.TextBody = processedBody;

        // Regular attachments
        foreach (var attachment in attachments)
        {
            if (attachment.PreloadedContent is not null)
            {
                builder.Attachments.Add(attachment.FileName, attachment.PreloadedContent);
            }
            else
            {
                var mimePart = new MimePart("application", "octet-stream")
                {
                    Content = new MimeContent(File.OpenRead(attachment.FullPath), ContentEncoding.Default),
                    ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
                    FileName = attachment.FileName
                };
                builder.Attachments.Add(mimePart);
            }
        }

        // Inline attachments (CID references in HTML body).
        // Content is preloaded once by the caller (see inlineAttachmentData in
        // RunSingleAsync) instead of File.OpenRead() per message — a fresh
        // MemoryStream per message still gives MimeKit an independent stream to
        // own/dispose per message without re-touching disk under load.
        if (isHtml)
        {
            foreach (var inline in inlineAttachments)
            {
                var cid = $"inline-{Guid.NewGuid():N}@mailloadtester";
                var mimePart = new MimePart("image", inline.Extension)
                {
                    Content = new MimeContent(new MemoryStream(inline.Content), ContentEncoding.Base64),
                    ContentDisposition = new ContentDisposition(ContentDisposition.Inline),
                    ContentId = cid,
                    FileName = inline.FileName
                };
                builder.LinkedResources.Add(mimePart);
                var nameNoExt = Path.GetFileNameWithoutExtension(inline.FileName);
                builder.HtmlBody = builder.HtmlBody?.Replace($"{{{{cid:{nameNoExt}}}}}", $"cid:{cid}")
                    ?? $"<img src=\"cid:{cid}\" />";
            }
        }

        message.Body = builder.ToMessageBody();

        foreach (var kv in options.CustomHeaders)
        {
            if (kv.Key.Equals("From", StringComparison.OrdinalIgnoreCase) ||
                kv.Key.Equals("To", StringComparison.OrdinalIgnoreCase) ||
                kv.Key.Equals("Subject", StringComparison.OrdinalIgnoreCase) ||
                kv.Key.Equals("Date", StringComparison.OrdinalIgnoreCase) ||
                kv.Key.Equals("Message-Id", StringComparison.OrdinalIgnoreCase) ||
                kv.Key.Equals("MIME-Version", StringComparison.OrdinalIgnoreCase) ||
                kv.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                continue;
            message.Headers[kv.Key] = kv.Value;
        }

        return message;
    }

    static bool IsTransient(Exception ex)
    {
        if (ex is SmtpCommandException sce)
            return (int)sce.StatusCode is >= 400 and < 500;
        if (ex is SmtpProtocolException) return true;
        if (ex is IOException or TimeoutException) return true;
        return false;
    }

    static TimeSpan GetRetryDelay(int attempt)
    {
        var baseMs = Math.Min(8_000, 500 * Math.Pow(2, attempt));
        var jitter = Random.Shared.NextDouble() * 0.4 - 0.2;
        return TimeSpan.FromMilliseconds(Math.Max(100, baseMs * (1 + jitter)));
    }

    static double? EstimateEta(int done, int total, DateTime start, Stopwatch sw)
    {
        if (done <= 0) return null;
        var elapsed = sw.Elapsed.TotalSeconds;
        if (elapsed < 0.5) return null;
        var rate = done / elapsed;
        if (rate <= 0) return null;
        return (total - done) / rate;
    }

    static double Percentile(double[] sorted, double p)
    {
        if (sorted.Length == 0) return 0;
        if (sorted.Length == 1) return sorted[0];
        var idx = p * (sorted.Length - 1);
        var lo = (int)Math.Floor(idx);
        var hi = (int)Math.Ceiling(idx);
        if (lo == hi) return sorted[lo];
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (idx - lo);
    }
}

/// <summary>
/// A write-only Stream that only counts bytes written and never buffers/stores
/// them. Used to measure a MimeMessage's serialized size for bandwidth throttling
/// without materializing the whole message (attachments included, already
/// base64-inflated) into a MemoryStream. With bandwidth limiting enabled, doing
/// that for every send attempt of every worker undermined the RAM budget that
/// AttachmentPlanner/EnsurePreloadSafe were specifically built to enforce
/// elsewhere — one extra full-size in-memory copy per attempt, per worker.
/// </summary>
sealed class CountingStream : Stream
{
    public long BytesWritten { get; private set; }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => BytesWritten;
    public override long Position { get => BytesWritten; set => throw new NotSupportedException(); }

    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => BytesWritten += count;
    public override void Write(ReadOnlySpan<byte> buffer) => BytesWritten += buffer.Length;

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        BytesWritten += count;
        return Task.CompletedTask;
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        BytesWritten += buffer.Length;
        return default;
    }
}
