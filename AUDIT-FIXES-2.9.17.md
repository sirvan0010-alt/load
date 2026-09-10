# 2.9.17 — performance hot-path fixes

## Progress Report throttling (P0)
`SmtpTestRunner.Report`:
- Always emits setup phases (0–2), terminal message steps (OK/FAIL/RETRY), path failures, observed snapshots.
- Routine phase-3 status is rate-limited (~125 ms).
- Path events: intermediate successes throttled; failures and DATA/QUIT still flow.

## TemplateTags (P1)
- Fast path when template has no `{`.
- `StringBuilder` + conditional replaces instead of unconditional chained `string.Replace`.
- Short random words via `stackalloc` where length ≤ 128.

## GUI
- OAuth2: tooltips clarify password field = access token.
- Dry-run: tooltip states pipeline is illustrative only.

## Still recommended on Windows
`dotnet test` / Release build / Mailpit smoke.
