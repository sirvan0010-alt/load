# MailSenderEngine — Future Tasks / Research Backlog

This document records ideas that may be evaluated later. They are backlog items, not current implementation requirements.

## 1. SMTP / identity rotation

**Status:** Deferred / research only.

Potential future use case:
- support multiple legitimately configured SMTP providers/accounts;
- route messages between configured identities according to explicit policy;
- maintain independent credentials, quotas and health state per provider/account.

Constraints:
- credentials remain external configuration/secrets;
- rotation must not be used to evade provider enforcement or sending limits;
- no automatic identity cycling solely because a provider throttles or blocks an account.

Before implementation, define a concrete legitimate requirement and test case.

## 2. Provider-limit bypassing

**Status:** Explicitly rejected.

Example of the mechanism we do **not** implement:

```text
Gmail limits the account
        ↓
change IP / account / identity
        ↓
continue at the same sending rate
```

Reason:
- this would turn rate limiting and identity management into a mechanism for bypassing provider controls;
- it conflicts with the architecture principle that provider limits are respected, not circumvented.

Instead, MailSenderEngine should react to provider limits with:
- adaptive rate limiting;
- bounded retry/backoff with jitter;
- provider/domain quotas;
- temporary pausing/deferment;
- explicit operational reporting.

## 3. Randomly increasing the number of SMTP connections

**Status:** Deferred / not implemented as an evasion mechanism.

Do not randomly increase SMTP connection count to compensate for throttling or blocking.

Legitimate future optimization is allowed:
- measure throughput and provider behavior;
- use bounded worker concurrency;
- use a bounded SMTP session pool when actual concurrency requires it;
- keep each SMTP session persistent and never concurrently use the same `SmtpClient` instance;
- tune connection count from measured workload/provider constraints, not randomness.

Target principle:

```text
Measured workload
      ↓
bounded concurrency
      ↓
bounded SMTP sessions
      ↓
provider/domain rate limits
```

## General rule

These tasks remain visible in the backlog so they are not forgotten. Their presence does not mean that the corresponding mechanisms should be implemented automatically.

For every future change, first establish:

1. the legitimate use case;
2. the provider/policy constraints;
3. the measurable performance or reliability requirement;
4. the smallest implementation that satisfies that requirement;
5. tests proving the behavior.
