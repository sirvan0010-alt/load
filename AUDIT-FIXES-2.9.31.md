# Audit fixes 2.9.31

## 1. SmtpConnectionPool.Return shutdown TOCTOU
`Return()` previously checked `_disposed` and enqueued the client in separate operations.
`DisposeAsync()` could drain `_idle` between them, leaving a leased client stranded in a disposed pool.
The disposed check and idle-queue hand-off are now protected by `_lifecycleLock`.

## 2. BandwidthLimiter large-message deadlock
The token bucket capacity was one second of bandwidth. A message larger than that capacity could never make `_tokens >= bytes`, resulting in an infinite delay loop.
The current message may define a larger temporary capacity, preserving the configured long-term rate while allowing large MIME messages to complete.
A regression test was added in `BandwidthLimiterTests`.

## Verification
Static source audit completed. This environment does not provide the .NET 8 SDK, so `dotnet test`/Release build could not be executed here.
