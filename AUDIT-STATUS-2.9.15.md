# Audit status 2.9.15

Static deep audit completed against the 2.9.14 source package.

## Fixed in this revision

- Random attachment transient RAM underestimation
- DNS compression parser termination/forward-pointer validation
- SMTP pool cancellation propagation
- Bogus CZ/EN realistic-data behavior and maintenance documentation

## Remaining verification

The following must be executed on a Windows machine with .NET 8 SDK:

```text
dotnet restore
dotnet test
dotnet build -c Release
```

Then perform GUI smoke tests for SMTP None/STARTTLS/implicit TLS, STOP, retries, pool reuse, adaptive concurrency, circuit breaker, per-recipient limits, profiles, random attachments, and Direct MX.

This document deliberately does not claim test results that were not executed.
