# 2.9.20 — IPv4 source rotation

## IpV4Rotator
- List: `192.0.2.10,192.0.2.11`
- CIDR: `192.0.2.0/28` (usable hosts only; max 4096 addresses)
- Round-robin (`GetNextIp`) or random (`GetRandomIp` / option `Ipv4RotationRandom`)

## Pool bind order
1. IPv6 prefix rotation
2. IPv4 rotation
3. Fixed SourceIp
4. Default OS route

## GUI
SMTP tab: "IPv4 rotace (seznam/CIDR)" + random checkbox.

Addresses must exist on a local interface (or OS allows bind).
