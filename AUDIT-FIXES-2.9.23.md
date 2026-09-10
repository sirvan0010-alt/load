# MailLoadTester 2.9.23 — hluboký audit a opravy

## Nálezy

### 1. ProtocolPathObserver — AsyncLocal pairing
Observer byl sice nově oddělen per SMTP klient, ale párování C:/S: příkazu a odpovědi bylo
uloženo v `AsyncLocal`. MailKit může volat logger z jiného execution contextu/threadu.
To mohlo ztratit pending krok a nesprávně obarvit pipeline.

**Oprava:** pending krok je nyní chráněn lockem a je vlastností konkrétního observeru.

### 2. SMTP pool — race při reuse idle klienta
`DisposeAsync()` mohl začít mezi health-checkem a předáním idle klienta workeru.
Worker mohl dostat již likvidovaný klient.

**Oprava:** druhá kontrola `_disposed` těsně před `MarkLeased()` a analogická kontrola
po reconnectu idle klienta.

### 3. Inline/CID přílohy obcházely RAM preflight
`File.ReadAllBytes()` načítalo inline přílohy přímo do RAM bez stejné ochrany jako
random attachments.

**Oprava:** před jakoukoliv alokací se nejprve sečtou velikosti a `EnsurePreloadSafe()`
je porovná s hard capem a aktuálním bezpečným RAM budgetem.

## Co jsem kontroloval navíc

- SmartPace cancellation a LinkedListNode reservations
- RateLimiter cancellation
- AdaptiveConcurrency cancellation/grant race
- SMTP pool permit ownership, duplicate Return/Discard, shutdown
- EML quota a body limit
- random attachment hard cap a MIME overhead
- MX transaction ID / QR / opcode / RCODE / TC / compression
- Direct MX multi-domain validation
- Circuit breaker cooldown/window
- profile version awareness
- dashboard binding na localhost
- webhook best-effort lifecycle
- custom header validation
- SMTPUTF8 transport option
- version consistency

## Testy

Přidány regresní testy pro:
- cross-thread pairing ProtocolPathObserver
- inline preload safety helper

**Poznámka:** `dotnet test` a Windows Release build musí být provedeny na systému s .NET 8 SDK.
