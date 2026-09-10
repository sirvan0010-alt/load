# MailLoadTester 2.9.11 — deep audit fixes

## Fixed

1. **AdaptiveConcurrencyLimiter deadlock** — tasks no longer use a permanent worker ID as a concurrency gate. A real async gate tracks active operations, so reducing the adaptive limit cannot strand workers waiting for a limit increase that only those same workers could trigger.
2. **Runtime version drift** — `AppVersion.Current` is now the single source for profile and webhook version fields.
3. **Attachment safety documentation** — corrected the stale 2% comment and ensured the computed budget never exceeds actual available physical memory.
4. **DNS cancellation correctness** — MX/SPF/DMARC cancellation is no longer swallowed and reported as an ordinary DNS miss.
5. **Direct-MX multi-domain safety** — validation now rejects mixed recipient domains because the previous implementation selected the MX of the first domain for all recipients.

## Confirmed from previous audit

- SmartPace cancellation removes the exact `LinkedListNode`, not `RemoveLast()`.
- Idle SMTP health-check uses `NOOP`.
- Random attachment generation is guarded by preflight before large allocation.

## Remaining audit items

- Per-recipient limit is checked before sending but counted only on success; concurrent attempts can temporarily exceed the configured cap. This should be converted to an explicit reservation/release model.
- Raw MX DNS parsing should validate transaction ID, response flags/RCODE and malformed compression more strictly.
- Auto-restart intentionally repeats the complete test and can therefore deliver duplicate messages; this must remain opt-in and should be labelled clearly in the UI.
- Full `dotnet test` and Windows Release build still require a .NET 8 SDK/Windows environment.
