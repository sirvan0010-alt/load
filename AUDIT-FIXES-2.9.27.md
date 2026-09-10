# MailLoadTester 2.9.27 — nezávislé prověření dvou paralelních auditů

Dva samostatné audity (dokumenty "audit 11" a "audit 12") prošly kód znovu a
přinesly 9 nálezů celkem, částečně se překrývajících. Každý jsem ověřil přímo
v kódu předtím, než jsem opravil — u žádného jsem nešel jen na slovo.

## Opraveno

1. **`AdaptiveConcurrencyLimiter.Reset()`** — `_active = 0` mazalo skutečně
   držené permity. Opraveno (Reset resetuje jen adaptivní statistiku, ne
   živé workery). Pozn.: `Reset()` se dnes nikde v runtime cestě nevolá
   (žádný caller), takže jde o opravu do budoucna, ne aktivní produkční bug.
   Regresní test přidán.
2. **`BandwidthLimiter`** — `(int)(need * 1000.0 / _bytesPerSecond)` mohlo
   přetéct `int` u velké zprávy + malého kbps limitu, `Math.Max(1, waitMs)`
   pak sklouzlo na 1ms a vznikl CPU spin místo dlouhého čekání. Opraveno
   rozdělením čekání do bezpečných max. 60s bloků.
3. **`SmtpTestRunner`** — `MemoryStream` materializující celou MIME zprávu
   (včetně příloh) jen kvůli zjištění velikosti pro bandwidth throttling,
   při každém pokusu o odeslání. Nahrazeno `CountingStream`, které bajty jen
   počítá bez alokace.
4. **`RblChecker`** — `SocketException` (NXDOMAIN i skutečná síťová chyba)
   se házelo do jednoho pytle jako "IP je čistá". Přidán `IsUnknown` stav
   rozlišený přes `SocketErrorCode` (HostNotFound/NoData = potvrzené
   NXDOMAIN, cokoliv jiného = neověřeno). GUI teď zobrazuje oranžový "?"
   místo zeleného "✓" u neověřeného výsledku.
5. **`DnsPolicyChecker`** — stejný problém pro SPF/DMARC. Přidány
   `SpfCheckFailed`/`DmarcCheckFailed`, GUI dialog rozlišuje "nemá záznam"
   od "nepodařilo se ověřit (DNS chyba)".
6. **`SmtpConnectionPool` — nová regrese ze včerejška, teď opravená.**
   `_clientCert?.Dispose()` v `DisposeAsync()` (2.9.26) mohlo teoreticky
   uvolnit certifikát uprostřed probíhajícího TLS handshake jiného workeru
   — klient není v `_leased` po celou dobu `EnsureConnectedAsync`. Přidán
   `_inFlightConnects` čítač; `DisposeAsync` počká na dokončení handshake
   (s bezpečným stropem) před uvolněním certifikátu.
7. **`WebhookNotifier`** — `HttpResponseMessage`/`StringContent` nikdy
   nedisposované. Opraveno pomocí `using`.
8. **Webhook + zavření okna** — `NotifyAsync(..., CancellationToken.None)`
   mohlo při zavření okna po dokončení testu (ale před dokončením webhooku)
   blokovat `FormClosing` až na 30s (HttpClient timeout). Navázáno na `_cts`.
9. **`CircuitBreaker.IsAnyOpen()` — TOCTOU race u cooldown resetu.**
   Podmínka „vypršel cooldown“ se vyhodnocovala MIMO zámek, se zastaralou
   hodnotou `since`. Pokud mezi tím vyhodnocením a získáním zámku proběhl
   souběžný `RecordFailure()`, který `_openSince["sliding-window"]` obnovil
   na nový čas, reset i tak proběhl a čerstvou chybu smazal. Opraveno —
   podmínka se teď znovu ověřuje uvnitř stejného zámku, který používá
   `RecordWindow()`. Regresní stres test (200 běhů, probabilistický).

## Metoda
Každý nález ověřen přímým čtením zdrojového kódu (ne převzat z auditního
textu bez kontroly) — u bodu 6 šlo navíc o regresi, kterou způsobila moje
vlastní předchozí oprava, a i tu jsem musel dohledat a opravit.

## Stále neověřeno
- Path traversal / UNC / symlink u uživatelských cest — nekontrolováno.
- Systematický cancellation audit celého Core — jen bodově.
- `dotnet test` / `dotnet build -c Release` — pořád nutné udělat u vás,
  v tomto prostředí není .NET SDK.
