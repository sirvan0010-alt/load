# MailLoadTester 2.9.39 — extracted reference

Source: user-supplied `MailLoadTester-2.9.39-FINAL.zip`.

The ZIP was inspected locally before importing. It contains a C#/.NET MailLoadTester solution with Core, GUI, and tests. This directory is a reference snapshot; it is not a dependency of MailSenderEngine and must not be merged into the production sender runtime.

## Architecture comparison

| Area | MailLoadTester 2.9.39 | MailSenderEngine target | Decision |
|---|---|---|---|
| SMTP library | MailKit | MailKit | KEEP |
| Persistent SMTP sessions | `SmtpConnectionPool` reuses clients | Persistent session/worker | KEEP, simplify first |
| Connection pool | Bounded pool + leases + lifecycle protection | Planned bounded session pool | ADOPT principle after measurement |
| Concurrency | `AdaptiveConcurrencyLimiter` | Worker concurrency + `SemaphoreSlim` | ADOPT bounded adaptive control later |
| Rate limiting | `RateLimiter` + `SmartPaceController` | Dedicated rate limiter | ADOPT, but provider-safe |
| Retry | Retry/backoff/greylist behavior in broader runner | `SmtpFailureClassifier` + bounded retry policy | ADOPT minimal 4xx retry first |
| Circuit breaker | Consecutive and sliding-window errors | Planned delivery protection | ADOPT later |
| Provider presets | Gmail/M365/Seznam/etc. | Planned provider/domain policy | ADOPT as configuration, not bypass logic |
| Observability | SMTP response/path/session logging | ILogger + delivery status | ADOPT |
| Queue | Test-run oriented execution | Durable delivery queue | Separate design; do not copy blindly |
| IP rotation | IPv4/IPv6 source rotation | Not part of normal delivery optimization | DO NOT use for limit evasion |
| Proxy rotation | SOCKS/HTTP proxy rotation | Not required | DO NOT adopt for bypass |
| Random content | Bogus/random HTML/attachments | Payload plugin architecture | Keep legitimate plugin separation |
| RBL checks | Spamhaus lookup | Not required for core sender | Optional diagnostics only |
| GUI | WinForms operational load-test GUI | UI decoupled from sender core | Reuse UX ideas, not coupling |

## Most valuable reference components

1. `SmtpConnectionPool.cs` — lifecycle, bounded leases, idle connection handling.
2. `AdaptiveConcurrencyLimiter.cs` — adaptive concurrency without busy waiting.
3. `RateLimiter.cs` — globally spaced reservations and cancellation-safe slot removal.
4. `SmartPaceController.cs` — combined pacing state machine; useful as a source of requirements, but our implementation should keep rate limiting, retry/backoff, and queue scheduling as separate concerns.
5. `CircuitBreaker.cs` — consecutive and sliding-window failure protection.
6. `ObservedResponses.cs` and `DeliveryPath.cs` — structured SMTP telemetry and visible protocol path.
7. Tests — useful concurrency/cancellation test patterns.

## Important architectural difference

MailLoadTester is a load-testing tool and contains features such as source-IP rotation and proxy rotation. Those mechanisms are not automatically appropriate for MailSenderEngine. Our sender must respect provider policies and configured limits. We can adopt the engineering techniques (bounded concurrency, persistent sessions, cancellation-safe scheduling, adaptive pacing, telemetry) without implementing provider-limit evasion.

## Imported snapshot policy

The complete ZIP is preserved by the user upload. This GitHub reference branch contains the extracted reference material and comparison documentation without making MailLoadTester a project dependency. Production changes continue under `MailSenderEngine/`.
