# Audit fixes 2.9.15

## Findings fixed

### 1. Random attachment RAM estimate undercounted peak memory
The previous preflight compared the MIME/base64 estimate to the safety budget, but the original per-message `byte[]` payloads remain live while MIME is being serialized/sent. The estimate therefore understated transient peak memory.

2.9.15 now accounts for:

- raw generated attachment bytes
- estimated MIME/base64 working copy (~37%)
- maximum configured attachments
- maximum configured concurrency

The auto-size calculation uses the same combined transient budget and fails closed if even the minimum safe size cannot fit.

### 2. DNS compression parser accepted an unterminated 32-jump chain
`TryReadName` now requires an actual zero-label termination and rejects forward compression pointers. Regression tests cover loops, forward pointers and non-terminating chains.

### 3. SMTP pool cancellation responsiveness
Cancellation during idle health-check or reconnect is no longer swallowed as a generic connection failure. The client is discarded and cancellation propagates immediately.

### 4. Bogus data documentation/behavior
Realistic data now mixes Czech and English Bogus locales per generated message and includes a company name. Documentation no longer claims deterministic generation; messages are correlated by test ID/token instead.

## Validation limitation

The source was statically audited and regression tests were added. A real `dotnet test` / Windows Release build still requires a Windows/.NET 8 SDK environment.
