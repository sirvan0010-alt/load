# Deep audit status — 2.9.14

This build incorporates the findings from the 2.9.12/2.9.13 audit plus an
additional pass over concurrency, shutdown, DNS parsing, RAM safety, profiles,
and version consistency.

## Fixed in 2.9.14
- SmartPace per-recipient reservation occurs before global pacing reservation.
- Cancellation during global pacing releases the recipient reservation.
- SMTP pool shutdown no longer disposes the semaphore while renters may wait.
- Return/Discard release the pool permit even when shutdown has started.
- MX parser rejects non-standard opcode and truncated UDP responses.
- MX parser requires exactly one question and validates compression pointer bounds.
- MX parser fails closed on malformed/truncated answer records and RDATA bounds.
- GUI title and installer version use the current 2.9.14 version.
- Added regression tests for the above.

## Still requires execution on Windows
- `dotnet restore`
- `dotnet test`
- `dotnet build -c Release`
- actual Windows GUI smoke test
- real SMTP integration test against a controlled test server

No claim is made that these commands passed in this environment because .NET 8
SDK is not installed here.
