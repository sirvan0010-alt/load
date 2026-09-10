# MailLoadTester 2.9.29 — deep audit fixes

## 1. CircuitBreaker per-category cooldown TOCTOU

`IsKeyOpen()` could read an expired timestamp and then call an unconditional
`TryRemove(key)`. A concurrent `RecordFailure()` could publish a fresh opening
between those operations; the stale checker could then delete the fresh state.

Fixed with conditional removal using the observed key/value pair. If another
thread wins the race, the fresh state is retained and evaluated.

## 2. SMTP client certificate disposal race

`SmtpConnectionPool.DisposeAsync()` previously used a bounded wait for
`_inFlightConnects` and then disposed `_clientCert` even if a TLS handshake was
still running.

That could invalidate the X509 certificate while MailKit was using it.

Fixed by waiting until the in-flight connection counter reaches zero before
disposing the certificate. Normal runner shutdown cancels active workers before
pool disposal, so the wait should normally complete promptly.

## Verification

Static source audit performed against the supplied package. The environment
does not provide the .NET 8 SDK, therefore `dotnet test` and a Windows Release
build could not be executed here.
