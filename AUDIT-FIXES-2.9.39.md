# MailLoadTester 2.9.39 — deep audit (final package this session)

Base: 2.9.38 (from upstream 2.9.32 deep-audit-fixed + continuum fixes).

## New in 2.9.39

### GUI: hardcoded options ignored profile values
`BuildOptions()` always forced:
- `IdleConnectionHealthCheckSeconds: 30`
- `CircuitBreakerWindowSize: 100`
- `CircuitBreakerFailurePercent: 90.0`

Profile load could not persist these. Now held in form fields `_idleHealthCheckSeconds`,
`_circuitWindowSize`, `_circuitFailurePercent` and restored on profile apply.

### FormatOptions (SmtpUtf8)
One `FormatOptions` instance per test run instead of `Clone()` per message.

### ProfileStore
Path traversal guard on Save/Load path.

## Previously confirmed (still in tree)
- CircuitBreaker conditional remove (TOCTOU)
- Pool `_inFlightConnects` wait without cert dispose timeout
- Idle health **0 = off**
- ObservedResponses immutable AddOrUpdate
- Adaptive cancel under lock
- Webhook cancel propagation
- MaxGreylistRetries wired
- Auto-restart metric aggregation
- ConnectivityTester ProxyClientFactory + socket dispose
- MX cache prune, IPv4 min /20, path traversal validation

## Deep pass this round — no additional defect found in
- Return/Discard single-permit (TryRemove gate)
- RentAsync outer catch releases gate
- PreWarm partial failure returns successful clients
- File.OpenRead via MimeContent (message using dispose chain)
- Progress report throttle (`MinReportIntervalMs`)
- IpV6Rotator bit-level host fill
- RateLimiter node-based cancel
- SmartPace global LinkedListNode reservation

## Still required on Windows
```
dotnet test
dotnet build -c Release
```
Smoke: Start → Stop during connect; Direct MX; proxy list; profile round-trip.
