# MailLoadTester — BUGS AUDIT

> Central ledger for verified defects found during code review of `main`.
> Only findings supported by the current repository code are recorded here.
> A finding is marked **OPEN** until source + tests are changed and the fix is verified.

## Status

| ID | Severity | Area | Status |
|---|---|---|---|
| BUG-001 | High | SmtpTestRunner / concurrency | OPEN |
| BUG-002 | High | SmtpTestRunner / auto-restart semantics | OPEN |
| BUG-003 | Medium | SmartPaceController / first-send pacing | OPEN |
| BUG-004 | Medium | ProxyRotator / random selection | OPEN |
| BUG-005 | Medium | IpV4Rotator / CIDR /31 | OPEN |
| BUG-006 | Medium | Repository structure / build integrity | OPEN |
| BUG-007 | High | SmtpTestRunner / retry pacing | OPEN |

---

## BUG-001 — `MaxConcurrency` is not enforced when adaptive concurrency is disabled

**Severity:** High  
**File:** `SmtpTestRunner.cs`  
**Status:** OPEN

### Evidence

The runner creates one `Task` for every message in a batch with:

`Enumerable.Range(batchStart, batchEnd - batchStart + 1).Select(async i => ...)`

The explicit concurrency gate is only acquired when `adaptive != null`. The SMTP pool has its own semaphore, but that semaphore limits SMTP connections, not the number of message tasks that can exist and wait/build state around the send pipeline.

Therefore, with `UseAdaptiveConcurrency = false`, a run such as `MessageCount = 10000` can create 10000 asynchronous message operations even when `MaxConcurrency = 1` or `2`.

### Impact

- `MaxConcurrency` does not represent actual worker/task concurrency.
- Large tests create unnecessary task/state pressure.
- Cancellation and scheduling overhead grow with `MessageCount`.
- The reported worker ID is only a modulo-derived label, not an actual bounded worker.

### Required fix

Use a bounded worker model (`MaxConcurrency` workers + indexed work distribution), or an equivalent bounded `Parallel.ForEachAsync`/channel design. Keep the SMTP pool as the connection/resource gate, not as the primary message-task limiter.

### Regression test

Add a test proving that the maximum number of simultaneously executing message operations never exceeds `MaxConcurrency` with `UseAdaptiveConcurrency = false`.

---

## BUG-002 — Auto-restart repeats the entire message set

**Severity:** High  
**File:** `SmtpTestRunner.cs`  
**Status:** OPEN

### Evidence

`RunAsync()` calls `RunSingleAsync(current, ...)` again after a majority-failure run. Each restart passes the original `MessageCount`; there is no tracking of which message indexes already succeeded or failed across runs.

The final result intentionally aggregates `sumSent` and `sumFailed`, which confirms that a restarted run represents another complete test execution rather than a continuation of the unfinished work.

### Impact

A partially successful test can resend messages that already succeeded. For a real SMTP destination this can produce duplicate deliveries and makes `MessageCount` ambiguous: it becomes the requested count **per attempt**, not the requested total count for the complete test.

### Required fix

Define and implement one explicit contract:

1. **Recommended:** auto-restart resumes only unfinished message indexes, preserving successful recipients/messages and a single global requested count; or
2. If full rerun is intentional, rename/document the option and result semantics explicitly as `retry whole test` and expose duplicate-delivery risk.

Add tests for a run such as 6 messages / 4 successes / 2 failures and verify the restart does not resend the 4 successful messages under resume semantics.

---

## BUG-003 — First global pacing slot waits one full interval

**Severity:** Medium  
**File:** `SmartPaceController.cs`  
**Status:** OPEN

### Evidence

`ReserveGlobalSlot()` initializes `nextAvailable` to `now` when the schedule is empty, then reserves:

`reservedSlot = nextAvailable + intervalTicks`

Therefore the first message is scheduled at `now + interval`, not immediately at `now`.

### Impact

For `IntervalMs = 1000`, the first message unnecessarily waits approximately 1 second. More importantly, the configured interval behaves like an initial startup delay as well as inter-message spacing.

### Required fix

When `_schedule` is empty, reserve the first slot at `now`; only subsequent slots should add `intervalTicks`. Preserve jitter/warm-up behavior and add a regression test for the first reservation.

---

## BUG-004 — Random proxy selection can falsely report that all proxies are blocked

**Severity:** Medium  
**File:** `ProxyRotator.cs`  
**Status:** OPEN

### Evidence

In random mode, `TryGetNext()` performs only `_all.Length` random picks and returns `null` if none of those picks happens to select an unblocked endpoint.

Random sampling with replacement does not guarantee visiting every endpoint. An available proxy can therefore exist while `TryGetNext()` incorrectly returns `null`.

### Impact

A test can abort with "all proxies temporarily disabled" even though at least one proxy is available.

### Required fix

In random mode, build/select from the currently unblocked endpoints deterministically (or perform a bounded shuffle/permutation without replacement). Keep selection thread-safe.

### Regression test

Block all but one proxy and repeatedly call `TryGetNext()` in random mode; it must never return `null` while the unblocked proxy exists.

---

## BUG-005 — IPv4 `/31` CIDR expansion drops one valid address

**Severity:** Medium  
**File:** `IpV4Rotator.cs`  
**Status:** OPEN

### Evidence

`ExpandCidr()` treats every prefix length `> 30` the same and yields only the parsed network address. For `/31`, that returns one address although `/31` point-to-point networks have two usable addresses under RFC 3021 semantics.

`/32` legitimately contains one address, so `/31` needs separate handling.

### Impact

A valid two-address `/31` source-IP configuration silently loses half of the configured address space.

### Required fix

Handle `/31` explicitly and return both addresses. Keep `/32` as one address. Add regression tests for `/31` and `/32`.

---

## BUG-006 — Repository contains committed Git-internal artifacts and a flattened/non-build-standard layout

**Severity:** Medium  
**Area:** repository structure / build  
**Status:** OPEN

### Evidence

The current `main` tree contains files that are Git internals or repository metadata rather than project source, including `HEAD`, `index`, `packed-refs`, `config`, `description`, `objects/`-related pack artifacts represented at root, and multiple `*.sample` hook files. It also contains placeholder-looking root entries such as `src`, `tests`, `installer`, and `mail-guardian` as regular blobs rather than normal directories.

At the same time, the actual C# source files and `.csproj` files are flattened at repository root.

### Impact

- The repository is difficult to build/reason about from a clean clone.
- IDE/project discovery can differ from the intended solution structure.
- Git metadata has been accidentally committed as project content.
- The repository tree does not match the intended `src/`, `tests/`, `installer/` architecture.

### Required fix

Reconstruct the repository from the canonical source tree, remove committed Git-internal artifacts, restore real directories, and verify `.sln` project paths and `ProjectReference` paths. Do not delete historical audit documents unless their content has been preserved elsewhere.

### Verification

`dotnet restore`, `dotnet build -c Release`, and `dotnet test -c Release` from a clean clone must succeed.

---

## BUG-007 — Retry path bypasses the global pacing controller

**Severity:** High  
**File:** `SmtpTestRunner.cs` / `SmartPaceController.cs`  
**Status:** OPEN

### Evidence

The normal send path calls `SmartPaceController.WaitBeforeSendAsync(...)`, which reserves the shared global pacing slot. The transient-error retry path, however, currently does only:

`await Task.Delay(GetRetryDelay(attempt), ct).ConfigureAwait(false);`

and then immediately starts the next attempt. There is no call back into the global pacing schedule after the retry backoff.

`SmartPaceController` explicitly documents `IntervalMs` as **global spacing for all workers**, so a retry is an SMTP send attempt and must participate in that same global spacing policy.

### Impact

A transient failure can cause a retry to be emitted immediately after its local exponential/backoff delay, independently of the global `IntervalMs`. With multiple workers/retries this can create bursts that violate the configured global pacing even though the normal first-attempt path is correctly throttled.

This also makes the effective send rate depend on the error pattern: under failures, the tester can become significantly more aggressive than the configured pacing suggests.

### Required fix

After the retry-specific backoff, re-enter the shared pacing controller before the retry attempt. The retry should participate in global pacing without reserving the same recipient twice. A dedicated method such as `WaitBeforeRetryAsync(CancellationToken)` is preferable so retry pacing does not corrupt per-recipient reservation state.

### Regression test

Use multiple concurrent workers with a small `IntervalMs`, force transient SMTP failures, and record attempt timestamps. Verify that retry attempts also obey the configured global spacing within the allowed timing tolerance.

---

## Review rules for this ledger

- Do not mark a defect fixed merely because a comment claims it is fixed.
- Verify source, callers, and tests together.
- Record exact file/path and the observable failure mode.
- Prefer a regression test before marking a code defect **FIXED**.
- Keep security/load-testing behavior bounded and explicitly authorized.
