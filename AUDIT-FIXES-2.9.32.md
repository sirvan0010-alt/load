# MailLoadTester 2.9.32 — deep audit fixes

## 1. SmtpConnectionPool: half-configured/new client leak

### Finding
A newly constructed `SmtpClient` was created before the main cleanup `try` block. Exceptions while configuring the client (certificate collection, proxy creation/selection, etc.) could therefore release the pool permit without disposing the client or removing its observer/proxy bookkeeping.

A second variant occurred after a successful connect: `DisposeAsync()` could begin before the final lifecycle check. The final check could then throw `ObjectDisposedException` before the client was registered as leased, while the outer permit-only catch did not dispose that client.

### Fix
Ownership now begins immediately after `SmtpClient` construction. All configuration, connect/authentication, and final lease registration are inside one cleanup boundary. If lease ownership is not successfully transferred to the pool, the client, protocol observer and proxy bookkeeping are cleaned up before the exception propagates.

## 2. AttachmentPlanner: overflow in peak-memory estimate

### Finding
`AttachmentPlanner.Prepare()` calculated:

`total * workerFactor * 1.5`

without checked/saturating arithmetic. With a sufficiently large collection of user-supplied files, the `long` calculation could overflow and produce an invalid small/negative estimate. That could make the RAM preloading decision incorrect.

### Fix
The peak estimate now uses saturating arithmetic and returns `long.MaxValue` on overflow. Such a configuration therefore fails the preload safety condition rather than accidentally passing it.

## Audit scope this round

- SmtpConnectionPool ownership/lifecycle around construction, configuration, connect, shutdown and permit release
- AttachmentPlanner numeric safety and preload decision
- BandwidthLimiter token accounting
- SmartPace reservation/cancellation
- CircuitBreaker TOCTOU/cooldown state
- AdaptiveConcurrencyLimiter waiter cancellation/permit ownership
- MainForm cancellation/webhook lifecycle
- MX resolver/cache/error classification
- SMTP session logger flush/dispose lifecycle

## Build limitation
The audit environment does not have the .NET 8 SDK installed, so `dotnet test` and `dotnet build -c Release` could not be executed here. Static source audit and package integrity checks were performed.
