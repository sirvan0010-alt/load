# 2.9.13 — MX parser hardening + profile version awareness

## MX DNS parser
- Transaction ID must match request
- QR bit must indicate response
- RCODE only NOERROR (0) or NXDOMAIN (3)
- Question QTYPE=MX, QCLASS=IN
- Compression pointers: bounds check, loop detection, max jumps
- NXDOMAIN → valid empty result (fallback A/AAAA)

## Profiles
- `IsVersionMismatch` compares file version to `AppVersion.Current`
- GUI warns when loading older/newer profile
- Missing JSON fields still get record defaults (soft migration)

## Circuit breaker
- Documented: `RecordSuccess` clears consecutive counters; sliding-window trip still needs cooldown
