# Deep audit status — 2.9.16

This build is based on 2.9.15 and contains another source-level concurrency, memory-safety and DNS-hardening pass.

## Critical areas reviewed
- SmtpTestRunner cancellation/retry/pool lifecycle
- SmartPace global reservations and per-recipient limits
- AdaptiveConcurrencyLimiter
- SmtpConnectionPool shutdown/rent/return/discard
- AttachmentPlanner and generated attachment allocation
- EML parsing and attachment decoding
- MX DNS parser and UDP response validation
- CircuitBreaker state transitions
- Profile version handling
- Dashboard lifecycle
- validation and GUI consistency

## Fixed in 2.9.16
- Per-recipient fixed-window rollover could erase live reservations: fixed with a real sliding window.
- EML attachments could bypass the normal RAM preflight: hard parser quotas added.
- `byte[file.Length]` was unsafe/non-compilable for a `long` length: explicit Int32 bound and cast added.
- Raw DNS response source was not checked: resolver endpoint validation added.
- GUI advertised 1024 MiB while planner intentionally blocked above 128 MiB: unified to 128 MiB.

## Not claimed
The package has not been honestly marked as `dotnet test passed` or `Release build passed` because those commands require a .NET 8 SDK/Windows build environment.
