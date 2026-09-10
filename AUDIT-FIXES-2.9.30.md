# MailLoadTester 2.9.30 — Deep Audit Fixes

## 1. SmtpConnectionPool lifecycle hand-off race

A TOCTOU window existed after an SMTP connection/authentication completed:

1. `RentAsync` checked `_disposed`.
2. `DisposeAsync` could set `_disposed`.
3. `RentAsync` then added the client to `_all`, marked it leased and returned it.

The same class of race existed for an idle client between the shutdown check and `MarkLeased`.

### Fix

The shutdown check and lease hand-off are now serialized with `_lifecycleLock`.

For a newly connected client, `_all.TryAdd`, `MarkLeased` and the final disposed-state check form one lifecycle-critical section.

For an idle client, the disposed-state check and `MarkLeased` are also paired under the same lock. If shutdown has already started, the client is forgotten/disposed instead of being returned.

This complements the existing `_inFlightConnects` protection: shutdown can no longer invalidate the lifecycle between connection completion and ownership transfer.

## 2. MainForm — SMTP Test Connection shutdown race

`Test Connection` previously used `CancellationToken.None`. Closing the WinForms window while this asynchronous operation was running could therefore leave it running after the form had started tearing down, with later UI/message-box access occurring after disposal.

### Fix

`OnTestConnectionAsync` now has its own:

- `CancellationTokenSource`
- completion `TaskCompletionSource`
- cancellation-aware `SmtpConnectivityTester.TestAsync`
- close/dispose guards before UI access

`FormClosing` cancels and waits for the connection test before allowing the form to close.

## Audit status

Static deep audit focus for this round:

- SMTP pool lifecycle and ownership hand-off
- shutdown/cancellation races
- WinForms asynchronous operation lifetime
- limiter permit ownership
- attachment preflight/allocation paths
- protocol logger lifecycle

The source package was inspected after modification. A real `dotnet test` / Windows Release build still requires a machine with the .NET 8 SDK.
