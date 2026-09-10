# MailGuardian — AI Engineering Instructions

## 1. Purpose

This file is the engineering contract for any AI agent, coding assistant, reviewer or developer working in this repository.

**Repository architecture:** one repository containing **two separate applications** with different responsibilities:

```text
mail-guardian/
├── MailGuardian/        # Python — email/EML OSINT and forensic analysis
└── MailSenderEngine/    # C#/.NET — legitimate email delivery engine
```

These applications are related at the product level but MUST NOT be collapsed into one runtime or one business-logic layer.

The repository is the source of truth. Never assume that a previous conversation, generated snippet or external audit accurately describes the current code.

---

## 2. Product boundaries

### MailGuardian

Python application for local email/EML investigation and evidence-oriented analysis.

Typical responsibilities include:

- `.eml` parsing;
- header and Received-chain analysis;
- authentication/result analysis such as SPF/DKIM/DMARC where available;
- email/header/MIME fingerprinting;
- similarity and clustering;
- IOC extraction;
- threat/confidence scoring;
- DNS/WHOIS and optional external enrichment;
- evidence and case management;
- SQLite persistence;
- Gmail import/synchronization where supported;
- CLI and Streamlit UI;
- reports and forensic evidence export;
- automated tests and CI.

MailGuardian is an **analysis tool**, not the SMTP sending engine.

### MailSenderEngine

C#/.NET application for legitimate, policy-compliant email submission and delivery workflow.

Responsibilities include:

- durable email queue;
- campaign/message scheduling;
- bounded worker concurrency;
- persistent MailKit SMTP sessions;
- provider/domain-aware rate limiting;
- retry and backoff;
- SMTP error classification;
- structured logging/metrics;
- cancellation and graceful shutdown;
- delivery/DSN/bounce processing;
- optional tracking only when explicitly required and appropriately configured;
- pluggable message generation through `IMailPayloadPlugin` where appropriate.

MailSenderEngine is **not** an EML forensic analyzer.

---

## 3. Do not merge the two applications

Do NOT:

- move Python analysis logic into C# merely for convenience;
- put SMTP sending into MailGuardian's Python modules;
- make Streamlit the control plane for SMTP delivery;
- duplicate the same domain/business logic in both applications;
- create a single giant application containing parser, threat analysis and SMTP transport;
- introduce cross-runtime coupling unless there is a clearly documented interface requirement.

If integration is later required, prefer explicit contracts such as a database/API/event boundary rather than shared implementation details.

Potential shared concepts may include stable identifiers such as `MessageId`, `CampaignId`, `RecipientId` and `CorrelationId`, but their semantics must be documented before integration.

---

## 4. MailGuardian — current code must be preserved and audited first

The current MailGuardian source originated as a packaged project and contains Python modules, tests, documentation, SQLite persistence, CLI/GUI functionality and optional external enrichment.

Before modifying it:

1. inspect the complete source tree;
2. inspect dependencies;
3. inspect all modules and their callers;
4. inspect tests and CI;
5. identify actual implemented functionality;
6. identify dead/duplicate code;
7. measure complexity and scalability risks;
8. only then refactor.

Do not rewrite working forensic logic merely to make the structure look different.

### MailGuardian audit checklist

The audit must explicitly cover:

1. **Project structure** — modules, entry points, namespaces/packages, responsibilities.
2. **Parser/EML handling** — parsing correctness, malformed input handling, MIME traversal, header normalization.
3. **Fingerprinting** — deterministic fingerprints, normalization, collision/false-positive considerations.
4. **Similarity** — algorithms, thresholds, candidate selection, complexity; pay special attention to `O(n²)` compare-all behavior.
5. **Threat/IOC analysis** — extraction quality, confidence, false positives, external enrichment failures.
6. **Evidence/Case model** — immutability/auditability, provenance, reproducibility, timestamps and identifiers.
7. **SQLite/data layer** — schema, indexes, transactions, migrations/versioning, concurrent access and recovery.
8. **Gmail connector** — authentication/token handling, incremental synchronization, failure/retry behavior and secret handling.
9. **CLI/GUI separation** — UI must call application logic rather than implement a second business implementation.
10. **Security/privacy** — secrets, PII, external API data, local storage permissions, logs and exported evidence.
11. **Testing/CI** — unit/integration coverage, malformed EML fixtures, regression cases, Python version matrix.
12. **Performance/scalability** — parsing cost, database queries, compare-all complexity, caching/indexing opportunities.
13. **Configuration/dependencies** — pinned/compatible dependencies, optional integrations and safe defaults.
14. **Dead/duplicate code** — obsolete generations, unused modules and duplicated logic.
15. **Documentation** — README, operational instructions, architecture and evidence semantics must match reality.

The audit output should be maintained as `MailGuardian/AUDIT.md` when appropriate.

Recommended status vocabulary:

- ✅ implemented and verified
- 🟡 partially implemented / needs hardening
- ⚠️ architectural or scalability risk
- ❌ missing
- 🔴 security defect

Never mark an item complete solely because a filename or function name suggests that it exists.

---

## 5. MailGuardian technical principles

Prefer:

- deterministic analysis;
- explicit data models;
- provenance for evidence;
- reproducible calculations;
- robust malformed-input handling;
- parameterized SQL;
- transaction boundaries that are explicit;
- indexes based on actual query patterns;
- bounded external API calls;
- clear separation between core analysis and enrichment;
- test fixtures representing real-world and adversarial EML variations.

For expensive similarity operations, do not blindly scale `compare-all`. First introduce candidate filtering/indexing if evidence shows that quadratic behavior is a real bottleneck.

External enrichment such as VirusTotal/AbuseIPDB/DNS/WHOIS must be optional and failure-tolerant. Core offline analysis must remain useful without those services.

Never silently alter forensic evidence. If normalization is performed, preserve the original value where evidence semantics require it and document the transformation.

---

## 6. MailSenderEngine technology

Preferred implementation language: **C#**.

Runtime: use the version actually defined by the project files. Do not upgrade blindly.

Libraries:

- MailKit
- MimeKit

Use asynchronous APIs throughout I/O paths.

Required properties:

- thread-safe shared state;
- `CancellationToken` propagation;
- bounded concurrency;
- no `.Result`/`.Wait()` in asynchronous execution paths;
- secure configuration;
- deterministic message state transitions;
- testable services instead of production logic in `Program.cs`.

---

## 7. MailSenderEngine target architecture

```text
MailSenderEngine
│
├── Application
│   ├── orchestration
│   └── message lifecycle
│
├── Queue
│   └── durable queue / repository
│
├── Scheduler
│   └── campaign/work scheduling
│
├── Workers
│   └── bounded worker pool
│
├── RateLimiting
│   ├── global
│   ├── provider
│   └── per-domain where justified
│
├── Retry
│   ├── SMTP classification
│   ├── backoff
│   └── jitter
│
├── SMTP
│   ├── session manager
│   └── persistent MailKit sessions
│
├── Payload
│   └── IMailPayloadPlugin
│
├── Delivery
│   └── DSN/bounce processing
│
└── Observability
    ├── structured logs
    └── metrics/health
```

The implementation may use fewer physical projects/folders initially, but responsibilities must remain separable.

---

## 8. MailSenderEngine SMTP sessions

Persistent SMTP sessions are preferred when supported by the provider:

```text
worker
  -> ConnectAsync
  -> AuthenticateAsync
  -> SendAsync(message 1)
  -> SendAsync(message 2)
  -> ...
  -> DisconnectAsync
```

Each `MailKit.Net.Smtp.SmtpClient` session has exclusive ownership by one worker. Never concurrently call SMTP operations on the same client.

Handle disconnects, idle timeouts, authentication failures and server throttling explicitly. Reconnect safely when appropriate.

Do not connect/authenticate/disconnect for every message unless a concrete requirement justifies it.

---

## 9. Concurrency, rate limiting and backoff

These are separate mechanisms.

### Concurrency
Maximum simultaneous SMTP sessions/operations. Use a bounded worker pool or equivalent synchronization.

### Rate limiting
Maximum send/submission rate over time. It must not be implemented merely as `Task.Delay` after arbitrary sends.

### Backoff
Reaction to temporary failures and provider throttling.

Target capabilities may include:

- global limit;
- provider limit;
- per-domain limit;
- maximum concurrency;
- minimum/maximum delay;
- exponential or decorrelated backoff;
- jitter;
- server-response-driven throttling;
- bounded recovery after successful operation.

The earlier prototype's `Volatile`/`Interlocked` adaptive delay is useful as a building block, but a single global delay is not the final rate-limiter architecture.

Do not implement evasion, stealth or bypass mechanisms. The system must remain policy-compliant.

---

## 10. Queue and state machine

The production sender must not rely on an in-memory `Task[]` or fixed batch partitioning as its durable execution model.

The durable queue should support, as appropriate:

- persistence;
- atomic claim/lease;
- attempt count;
- scheduled retry time;
- status;
- timestamps;
- error information;
- idempotency/correlation identifiers;
- restart recovery;
- safe lease release/expiry.

Suggested lifecycle:

```text
QUEUED
  -> SENDING
  -> ACCEPTED
  -> DELIVERED
  -> OPENED
  -> CLICKED

SENDING
  -> RETRY_WAIT
  -> QUEUED

SENDING
  -> FAILED
  -> BOUNCED
```

Do not claim that `ACCEPTED` means final delivery. SMTP `250` generally means the server accepted the message for further processing.

State transitions must be explicit, validated and idempotent.

---

## 11. Retry/error classification

At minimum distinguish:

- success/accepted;
- temporary SMTP `4xx`;
- permanent SMTP `5xx`;
- connection/TLS errors;
- authentication failures;
- timeout;
- cancellation;
- local message-generation/serialization errors.

Retry policy must be bounded and configurable. Permanent recipient failures should normally not be retried indefinitely.

Every retry retains the same logical message identity and increments an attempt counter.

Never implement infinite retries.

---

## 12. Message generation and plugin architecture

Transport must not contain campaign-specific business rules.

Where the repository supports it, use:

```csharp
public interface IMailPayloadPlugin
{
    Task<MimeMessage> BuildAsync(
        MailPayloadContext context,
        CancellationToken cancellationToken);
}
```

Before introducing or changing this interface, inspect the actual source for an existing equivalent. Do not create duplicate abstractions.

---

## 13. Delivery, DSN and tracking

SMTP acceptance is not final delivery.

DSN/bounce processing is a separate concern from SMTP submission.

Open/click tracking is optional. It must not be silently added to messages and must consider privacy, consent, client blocking and legal requirements.

Tracking is disabled by default unless explicitly required.

---

## 14. Security

NEVER commit:

- SMTP passwords;
- API keys;
- OAuth client secrets/tokens;
- private keys;
- credential-bearing connection strings;
- production recipient lists;
- unnecessary customer PII.

Use environment variables, configuration providers or secret stores.

If credentials are found in historical/source content, treat them as a security defect and do not copy them into new commits. Recommend rotation when applicable.

Do not log secrets or authorization headers.

---

## 15. Cancellation and shutdown

Propagate `CancellationToken` through asynchronous pipelines where supported.

Shutdown sequence:

1. stop accepting new work;
2. stop claiming additional queue items;
3. allow safe in-flight sends to complete when possible;
4. persist/release queue leases;
5. disconnect SMTP sessions cleanly;
6. exit without corrupting state.

Do not treat normal `OperationCanceledException` as an unexpected application failure.

---

## 16. Testing

### MailGuardian
Test at minimum where applicable:

- valid and malformed EML;
- MIME edge cases;
- header normalization;
- fingerprint determinism;
- similarity thresholds;
- IOC extraction;
- SQLite persistence/recovery;
- evidence/case integrity;
- external enrichment failures;
- CLI/GUI behavior through shared application logic.

### MailSenderEngine
Test at minimum where applicable:

- SMTP success;
- temporary `4xx`;
- permanent `5xx`;
- connection/TLS failure;
- authentication failure;
- retry limit;
- backoff/jitter;
- cancellation;
- duplicate/idempotent processing;
- queue recovery;
- concurrent workers;
- rate limiter boundaries;
- configuration validation;
- SMTP session reconnect.

Network tests must use mocks/fakes or a dedicated local test SMTP server. Never require production credentials.

---

## 17. Documentation and audit

Architecture changes must update relevant documentation.

The repository should eventually contain:

```text
README.md
ARCHITECTURE.md
AI_INSTRUCTIONS.md
MailGuardian/AUDIT.md
MailSenderEngine/README.md
```

The audit must distinguish verified implementation from planned functionality.

External AI audits are input, not authority. Verify every claim against source code.

---

## 18. Mandatory AI workflow

Every AI agent must follow:

### A — Inspect
Inspect repository tree, project files, dependencies, source, tests, CI and documentation.

### B — Report
Before major changes, report current state, working functionality, gaps, risks and intended files.

### C — Implement
Make the smallest coherent change that advances the architecture.

### D — Validate
Build, run tests, inspect warnings/errors, check cancellation/error paths and review the diff.

### E — Document
Update architecture/audit/operational documentation when behavior changes.

### F — Commit
Use a descriptive commit message describing the actual change.

Never claim completion without validation.

---

## 19. Anti-patterns

Do NOT:

- hard-code credentials;
- mix MailGuardian forensic logic with SMTP transport;
- create per-message SMTP connections without justification;
- use unlimited parallel tasks;
- retry permanently failed messages forever;
- treat one global delay as complete rate limiting;
- treat SMTP `250` as final delivery;
- put all production logic in `Program.cs`;
- rotate identities/headers to evade provider controls;
- bypass spam/abuse controls;
- implement stealth/evasion behavior;
- log secrets;
- delete working analysis functionality without understanding it;
- rewrite both applications in one uncontrolled refactor.

---

## 20. Definition of done

A change is complete only when:

- it is in the correct application/layer;
- responsibilities remain separated;
- asynchronous/cancellation behavior is correct where applicable;
- shared state is thread-safe where applicable;
- configuration is secure;
- errors are handled/classified correctly;
- important failure paths have tests;
- documentation is updated where needed;
- build/tests pass;
- resulting diff is reviewed;
- no unrelated behavior was silently changed.

---

## 21. Roadmap

### Phase 1 — MailGuardian
1. Normalize repository layout.
2. Complete forensic audit and write `MailGuardian/AUDIT.md`.
3. Fix only verified defects and high-value scalability/security issues.
4. Preserve and improve existing tests/CI.

### Phase 2 — MailSenderEngine
1. Create clean C#/.NET project.
2. Establish configuration/options and secure environment handling.
3. Introduce domain models and explicit message state machine.
4. Implement `IMailPayloadPlugin` boundary.
5. Implement SMTP session manager with persistent MailKit sessions.
6. Implement durable queue.
7. Implement bounded workers.
8. Implement rate limiter.
9. Implement retry/backoff and SMTP classification.
10. Add structured logging/metrics/health checks.
11. Add DSN/bounce processing.
12. Add optional tracking only if explicitly required.
13. Add integration/load tests and operational documentation.

### Phase 3 — optional integration
Only after both applications are stable, evaluate whether a documented API/database/event contract adds real value. Do not couple them merely because they share the word “mail”.

---

## Final rule

**MailGuardian analyzes mail. MailSenderEngine sends mail.**

One repository may contain both, but they are two applications with independent responsibilities and lifecycles.

Inspect first. Understand second. Change third. Validate fourth.
