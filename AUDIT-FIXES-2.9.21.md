# 2.9.21 — Proxy rotation + ban detection

## Proxy
- ProxyClientFactory: HTTP / SOCKS4 / SOCKS5
- ProxyRotator: round-robin or random, temporary ban
- ProxyList option + GUI
- Legacy single SOCKS5 still works

## Ban detection
- IpBanDetector heuristics (550 5.7.1, blacklist, spamhaus, rate limit…)
- On match: ReportProxyBlocked + Discard (no return to idle pool)

## IPv6 CIDR
- Already in 2.9.19 (IpV6Rotator) — no change required
