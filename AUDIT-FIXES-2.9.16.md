# MailLoadTester 2.9.16 — deeper audit fixes

## Fixed

### 1. Per-recipient sliding-window race
The previous implementation reset `Reserved` when the fixed window rolled over. An in-flight reservation could therefore disappear and allow more than the configured maximum. The implementation now keeps committed send timestamps in a true sliding window and never clears in-flight reservations merely because time crossed a window boundary.

### 2. EML attachment memory safety
EML attachments were decoded to `byte[]` before the normal attachment safety planner ran. The parser now has hard limits on EML file size, decoded attachment bytes and body length, and uses a quota stream while decoding attachments.

### 3. Large-file preload compile/safety edge
The attachment preloader now rejects files larger than `Int32.MaxValue` before creating a `byte[]` and explicitly casts the checked length.

### 4. DNS UDP response source validation
MX resolution now verifies that the received UDP response came from the DNS server that was queried. Transaction-ID validation remains in place as a second check.

### 5. Random attachment UI/API consistency
The supported explicit generated-attachment size is now consistently capped at 128 MiB in GUI and validation. The planner already enforced that per-attachment safety cap; the UI no longer advertises impossible 1024 MiB values.

### 6. Random-data documentation
Documentation now correctly states that the generator is randomized and traceable by test ID/token; it is not deterministic across runs.

## Test additions
- EML file-size quota
- EML decoded-attachment quota
- Existing SmartPace recipient reservation and concurrency regression tests retained

## Build status
A real `dotnet test` / Windows Release build must still be performed in an environment with the .NET 8 SDK and Windows desktop tooling.

### 7. Parallel SMTP protocol pipeline attribution
The pipeline observer was previously shared by all SMTP workers. Concurrent protocol streams could overwrite the pending step and workers could drain each other's events. The pool now creates one observer per SMTP client and exposes per-client draining; the observer also isolates pending state with `AsyncLocal`.

### 8. Large synthetic attachment generation
PNG/JPEG generation previously used intermediate `List<byte>`/temporary arrays that could multiply the peak allocation for a large payload. Generation now writes directly into the final target `byte[]`, reducing transient memory pressure and preserving the requested exact size.

### 9. SMTP pool ownership and pre-warm failure paths
The pool now tracks leased clients explicitly. Duplicate `Return`/`Discard` calls cannot inflate the semaphore or enqueue the same client twice. `PreWarmAsync` also returns successfully warmed clients if another warm-up task fails.

A shutdown-time double-release path in `RentAsync` was removed as part of the same ownership audit.

### 10. Circuit-breaker cooldown reset
A sliding-window breaker retained its old ring after cooldown. It could therefore re-trip immediately on the first post-cooldown result. Cooldown now clears the old window and starts a fresh measurement window.

### 11. SMTPUTF8 transport semantics
The GUI option previously injected a non-standard `SMTPUTF8: yes` message header. SMTPUTF8 is negotiated at the SMTP transport layer. The runner now enables MimeKit `FormatOptions.International` when the option is selected instead of adding a bogus MIME header.

### 12. Runner resource lifecycle
Session logging and the dashboard were created before some operations that could throw (attachment preflight / pool construction / pre-warm), while the main `try/finally` started too late. The runner now creates the pool inside the main guarded block and disposes pool, session logger and dashboard from the same finalizer, including pre-warm failures.

### 13. Runtime RAM re-check for generated attachments
The initial RAM preflight is now supplemented by a per-message safety re-check immediately before large random attachment generation. This handles the case where available physical RAM falls materially during a long-running test.
