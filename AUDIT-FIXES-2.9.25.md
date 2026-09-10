# MailLoadTester 2.9.25 — audit fixes

## Pool lifecycle / permit ownership

`SmtpConnectionPool.DisposeAsync()` no longer removes leased clients from `_leased` and `_all` during shutdown. A leased client remains owned by its worker until `Return()` or `Discard()`.

This preserves the invariant:

`Rent -> exactly one permit -> Return/Discard -> exactly one permit release`

The runner already awaits all worker tasks before disposing the pool, but the pool itself now remains safe if shutdown overlaps a leased client.

Added regression test: `DisposeKeepsLeasedPermitOwnedUntilReturn`.

## Session logger concurrent flush

`SmtpSessionLogger.Flush()` is now serialized with a dedicated `_flushLock`. This covers the race between the periodic timer callback and `Dispose()`'s final flush. Snapshot, disk append and failed-write requeue now form one serialized transaction.

Failed writes still requeue the snapshot with a bounded 4 MiB buffer and do not propagate exceptions into the SMTP run.
