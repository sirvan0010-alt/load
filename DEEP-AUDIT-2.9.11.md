# MailLoadTester 2.9.11 — deep audit

Audit scope: Core concurrency, cancellation, SMTP pool, pacing, adaptive concurrency, circuit breaker, attachment memory safety, random data, DNS/RBL, profiles/webhooks, direct-MX, GUI/version consistency and tests.

## Fixed in this build

### Critical / high
- **Adaptive concurrency deadlock risk:** replaced permanent `workerId > adaptive.Current` waiting with `AdaptiveConcurrencyLimiter.AcquireAsync/Release`. A reduced adaptive limit can no longer strand workers behind a limit that only those workers could increase.
- **SmartPace cancellation ownership:** confirmed exact `LinkedListNode` removal; no `RemoveLast()` cancellation remains.
- **DNS cancellation swallowing:** SPF/DMARC and MX resolution now rethrow cancellation instead of silently reporting a DNS miss/fallback.
- **Direct MX mixed-domain correctness:** validation now rejects multiple recipient domains in Direct MX mode instead of sending all recipients through the first domain's MX.

### Medium
- **Version drift:** added `AppVersion.Current = 2.9.11`; profile and webhook runtime versions now use it; GUI title updated.
- **Attachment safety documentation/calculation:** stale 2% comment corrected; safety budget is explicitly clamped to actual available physical RAM.
- Added adaptive concurrency regression tests and maintained SmartPace cancellation regression coverage.

## Findings intentionally left for the next pass

1. **Per-recipient limit concurrency semantics (medium/high):** the check happens before sending, but the counter is incremented only after success. Several concurrent workers can therefore pass the check for the same recipient and temporarily exceed `MaxMessagesPerRecipient`. Recommended fix: explicit per-recipient reservation + release/commit semantics.
2. **Raw MX parser robustness (medium):** validate DNS transaction ID, QR/RCODE, answer type/class and compression pointers more strictly. Current bounds checks prevent obvious out-of-range reads, but a malformed/spoofed UDP answer can still be interpreted too permissively.
3. **Auto-restart semantics (medium):** when enabled, the complete requested test is repeated after a majority failure, so messages may be delivered more than once. It is opt-in by default; UI should make the duplicate-send consequence explicit.
4. **Circuit-breaker semantics (medium):** `RecordSuccess()` clears all category failure counters. This is coherent as a global breaker policy but should be documented as such if category-specific diagnosis is expected.
5. **Profile schema migrations (low/medium):** profiles from older versions are accepted without explicit migration/version validation. This is convenient but can silently accept stale defaults after new fields are introduced.
6. **Dashboard is intentionally localhost-only:** no remote authentication is provided. This is a safe default; do not expose it on `0.0.0.0` without authentication.
7. **RBL is informational only:** DNS-based Spamhaus Zen checks can have policy/usage constraints; the tool must not treat DNS failure as proof of clean status.

## Verification limitation

The environment used for this source audit does not have the .NET 8 SDK, so `dotnet test` and a Windows Release build were not executed here. Static source checks and regression-test code were reviewed. Final acceptance requires running `dotnet restore`, `dotnet test` and a Release build on Windows/.NET 8.
