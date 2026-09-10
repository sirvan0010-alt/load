# MailLoadTester – maintenance contract for future AI/code updates

## Source of truth

Before modifying the project, read this file together with `README-BUILD.md`, `Models.cs`,
`AttachmentPlanner.cs`, `RandomTestData.cs` and `SmtpTestRunner.cs`.

Do not infer requirements only from the GUI. The Core options and runner behavior are the
source of truth.

## Message randomization requirements

The random-data system must support these independent switches:

- `UseBogusData`: realistic synthetic names/product/text.
- `GenerateRandomHtml`: generate a complete HTML body per message.
- `GenerateRandomAttachments`: generate 1..`MaxRandomAttachments` tiny valid test files per message.
- `VarySubjectBodyPerMessage`: ensure every message has an ID/token variation even when the
  full random-data mode is disabled.
- `MaxRandomAttachments`: hard upper bound, 1..5.

These options are backward compatible: all default to disabled and the old `Create(int id)`
API must continue to work.

## Safety and testability invariants

1. **Never randomize or overwrite `MailTestOptions.From` automatically.** The From address is
   user input and must remain unchanged.
2. Generated data is synthetic only; do not use real personal data.
3. Every generated message gets a unique `MLT-<message-id>-<token>` marker.
4. Generated attachments are intentionally tiny. They must not bypass the attachment-memory
   strategy or create uncontrolled RAM usage at high concurrency.
5. User-supplied attachments remain governed by `AttachmentPlanner`.
6. Per-message generated attachments are bounded by `MaxRandomAttachments` and are scoped to
   that message/worker; they are not placed into a global unbounded cache.
7. HTML content must be HTML-encoded where user/generated text is interpolated.
8. Randomization must be thread-safe under MaxConcurrency 1..20.
9. Do not add automatic From-domain rotation. If a future feature adds randomized From, it
   must be an explicit opt-in and obey Test Mode/AllowedDomains.
10. Keep `MailTestOptions` optional parameters backward compatible whenever possible.
11. Never allocate large generated attachment payloads before safety preflight. Call
    `AttachmentPlanner.EstimateRandomAttachments(...)` before generating requested sizes.
12. Random attachment safety is intentionally conservative: estimate worst-case
    `size × max attachments × MaxConcurrency` plus ~37% MIME/base64 overhead, compare it
    with a bounded fraction of available memory and a hard transient-payload cap.
13. GUI must explain the safety calculation in a tooltip/info label. A dangerous manual size
    must be rejected before the first large allocation; never silently clamp a user's requested
    test size and pretend it was honored.
14. 0/Auto should choose a conservative per-attachment size from current available memory,
    concurrency and attachment count.

## Architecture

The intended pipeline is:

GUI
  -> MailTestOptions
  -> SmtpTestRunner
  -> RandomTestData / AttachmentPlanner
  -> MIME
  -> MailKit SMTP

The GUI must not duplicate message-generation logic.

## Testing requirements

Any change to randomization must test:

- legacy `Create(int id)`;
- Bogus mode;
- HTML mode;
- random attachment count 1..5;
- unique message IDs/tokens;
- valid PNG/JPEG/PDF payloads;
- thread-safe generation under parallel calls;
- no mutation of `From`;
- attachment memory behavior with high concurrency.

Do not claim tests passed unless `dotnet test` was actually executed in a .NET 8 environment.


## Random attachment size safety (2.9.3)

The GUI exposes `RandomAttachmentSizeMb`: `0` means Auto, otherwise the user requests the
size of each generated attachment in MB (1..1024). This is a test-size request, not a
guarantee that an unsafe allocation will be performed.

Before the first generated attachment is allocated, the runner performs a safety preflight.
The rough model is:

`requested size × MaxRandomAttachments × MaxConcurrency × 1.37 MIME/base64 overhead`

The result is compared with a conservative fraction (15%) of currently available physical
RAM on Windows, with a hard transient generated-payload cap of 1 GiB and a per-attachment
cap of 128 MiB. If unsafe, the test is rejected before large allocations. The GUI shows the
calculation and available RAM in the Message tab, and the same explanation is available in
the tooltip.

Auto mode chooses a conservative size from the current available RAM, concurrency and
attachment count. It must never silently turn an unsafe manual request into a smaller test;
manual unsafe requests are rejected so the user knows the requested scenario was not run.

### Example for a 16 GB PC

The calculation uses currently available physical RAM, not the nominal installed RAM. For
example, if Windows reports about 11 GB currently available, the safety budget is limited by
the 1 GiB hard cap. With 20 workers and 2 generated attachments, Auto will typically choose
a conservative size around 13 MB per attachment. The exact value changes with current free
RAM, concurrency and attachment count. A manual value such as 1000 MB will normally be
rejected long before any 1000 MB byte array is allocated when the worst-case estimate is unsafe.

## SmartPaceController reservation safety

`SmartPaceController` uses one global `LinkedList<long>` schedule shared by all workers.
Every reserved slot MUST keep its own `LinkedListNode<long>` reference for the lifetime of
the wait. On cancellation or completion, remove that exact node. Never use `RemoveLast()`,
`RemoveFirst()` or any position-based removal to cancel a worker reservation: another worker
may have added a reservation concurrently, and deleting the wrong slot breaks the global
spacing guarantee.

Any change to `ReserveGlobalSlot()` / `WaitBeforeSendAsync()` must include a concurrency test
that creates at least two reservations, cancels the first, and verifies that the second
reservation remains scheduled.

## Deep-audit rules added in 2.9.11

- Adaptive concurrency must use an actual async gate; never gate queued work by a permanent worker ID.
- Cancellation of a specific reservation must remove that exact reservation node; never use `RemoveLast()`/`RemoveFirst()` for ownership-specific cancellation.
- DNS helpers must propagate `OperationCanceledException`; cancellation must never be converted into a normal DNS miss.
- Direct-MX mode must not silently use one domain's MX server for recipients belonging to another domain.
- Runtime-facing version strings must use `AppVersion.Current`; do not hard-code a release number in profiles, webhooks or GUI title text.
- Attachment safety budget must never exceed actual currently available physical memory.


## 2.9.15 audit rules
- Never consume a global SmartPace slot before a per-recipient reservation is available.
- A cancelled global pacing wait must release the recipient reservation it owns.
- Raw DNS parsing must fail closed on truncation, invalid opcode, invalid compression pointers, and malformed RDATA.
- Do not hard-code the application version in GUI/installer when AppVersion.Current is available.


## Random attachment memory invariant

The RAM preflight must account for both the generated raw `byte[]` payloads and the additional MIME/base64 working memory while the message is being sent. Comparing only the encoded MIME estimate against the safety budget is insufficient.


## Deep-audit rules added in 2.9.16

- Per-recipient limits use a true sliding window. Never reset in-flight reservations merely because a time window rolled over.
- EML parsing is safety-sensitive: enforce file, body and decoded-attachment quotas before exposing attachment byte arrays to the runner.
- Raw UDP DNS responses must be accepted only from the resolver endpoint that was queried, in addition to transaction-ID validation.
- Any `FileInfo.Length` used to allocate a byte[] must be bounds-checked against `Int32.MaxValue` before the cast/allocation.
- GUI limits must match Core limits; do not advertise values that the planner will always reject.
- Protocol pipeline observers must be scoped per SMTP connection. Never use one mutable pending-command state or one event queue for multiple parallel SMTP streams.
- Large synthetic attachments should be generated directly into the final payload where possible; avoid `List<byte> -> ToArray()` or whole-payload temporary copies.
- SMTP pool permits are ownership tokens: every successful `RentAsync` creates exactly one leased ownership, and exactly one `Return` or `Discard` may release it.
- Pre-warm failure must clean up every successfully completed rent before propagating the failure.


## 2.9.23 audit rules
- Never use AsyncLocal for SMTP protocol command/response pairing; each observer is per client and must synchronize pending state explicitly.
- Never call File.ReadAllBytes for user-controlled attachments before AttachmentPlanner/EnsurePreloadSafe.
- Never hand an idle SMTP client to a worker after pool disposal has begun.
