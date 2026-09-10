# 2.9.19 — IPv6 source rotation

## IpV6Rotator
- Generates random IPv6 addresses inside a configured prefix (default /64).
- Wired in `SmtpConnectionPool.EnsureConnectedAsync`: each **new** connection binds a fresh address.
- Takes precedence over fixed `SourceIp`.
- Options: `Ipv6Prefix`, `Ipv6PrefixLength` (1–128).
- GUI: SMTP tab fields + tooltips.
- Requires the host OS to route/bind the prefix; otherwise connect fails and the pool discards the client.

## Note
Idle connection reuse keeps the same bound address until Discard/new client.
