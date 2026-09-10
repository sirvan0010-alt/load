# MailGuardian UI Design

## SMTP Send Panel

The initial UI concept for the SMTP sending screen is intentionally compact and operationally focused:

```text
Interval: [ 500 ] ms

       [ TEST SMTP ]    [ SEND ]

████████████████░░░░  72%

Sent: 72    Failed: 1    Time: 00:00:38
```

## Design goals

- Clear separation between SMTP connectivity testing and the actual send operation.
- Interval/rate control is visible and editable before sending.
- Progress is immediately visible during an active run.
- Sent/failed counters and elapsed time provide at-a-glance runtime status.
- Keep the panel suitable for a future asynchronous `CancellationToken`-aware send pipeline.
- UI must remain decoupled from SMTP/network implementation.

## Planned states

1. **Idle** — interval editable, `TEST SMTP` and `SEND` available.
2. **Testing** — SMTP test in progress; send action temporarily disabled.
3. **Sending** — progress, counters and elapsed time update asynchronously.
4. **Completed** — final counters and duration remain visible.
5. **Cancelled/Failed** — preserve partial statistics and show the reason without losing runtime data.

## Implementation notes

The design is a reference UI specification, not a commitment to a specific UI framework. The presentation layer should consume a thread-safe runtime/status model rather than directly controlling SMTP sessions.

Any future rate limiting should enforce provider limits and configured safety constraints rather than attempting to bypass provider restrictions.
