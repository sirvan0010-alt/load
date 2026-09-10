# Audit chain (for continuing AI / maintainers)

1. Read `VERSION` and `AppVersion.Current` — must match.
2. Read the highest `AUDIT-FIXES-2.9.xx.md`.
3. Never re-introduce:
   - Timeout before `_clientCert.Dispose()` while `_inFlightConnects > 0`
   - Unconditional `TryRemove` on circuit cooldown expiry
   - In-place mutation in `ConcurrentDictionary.AddOrUpdate` update factory
   - `IdleConnectionHealthCheckSeconds == 0` meaning always NOOP
   - Hardcoded circuit/idle values in `BuildOptions` that ignore profile fields
4. After changes: bump VERSION + AppVersion + new AUDIT-FIXES file.
5. `dotnet test` + Release build before calling it a release.
