# MailLoadTester 2.9.12 — per-recipient reservation

## Fixed

### Per-recipient limit race (from deep audit 2.9.11 remaining items)

Previously:
1. WaitBeforeSend checked `Count >= Max`
2. Multiple workers could pass concurrently
3. SMTP send
4. Count++ only on success

Now:
1. `WaitBeforeSendAsync` waits until a slot is free
2. `TryReserveRecipient` increments `Reserved` under lock (Count+Reserved < Max)
3. Success → `CommitRecipient` (Reserved--, Count++)
4. Failure/cancel → `ReleaseRecipient` (Reserved--)

Regression tests: `PerRecipientLimitTests`.

### Auto-restart UI

Tooltip clearly warns that the entire test is repeated and messages may be delivered more than once.

### Version

`AppVersion.Current` = 2.9.12

## Still open (from 2.9.11 audit)

- Stricter raw MX DNS parser validation
- Profile schema migration layer
- CircuitBreaker RecordSuccess resets all categories (documented behavior)
