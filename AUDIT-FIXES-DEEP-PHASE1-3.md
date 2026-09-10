# Hloubkový audit — Fáze 1–3 (fixes)

## Fáze 1 — Payload / GC

### EmlTemplateParser.cs
1. `message.HtmlBody` se čte 2x (getter není cachovaný v MimeKitu, každé volání znovu
   dekóduje celé tělo) → čte se jen jednou.
2. `MimeMessage` (IDisposable) se nikdy nedisposoval → `using`.
3. `MemoryStream` bez počáteční kapacity → roste zdvojnásobováním + `ToArray()` dělá
   ještě jednu plnou kopii → dočasně ~2× velikost přílohy na LOH. Přidán odhad
   počáteční kapacity ze zdrojového (zakódovaného) streamu.

### RandomTestData.cs
4. Zbytečná instance `new Faker("en")` se vytvořila a hned zahodila při
   `useBogusData=true` (přepsána o pár řádků níž) → odstraněno.
5. **Bug (pád aplikace):** `CreatePaddedPng` — nedostatečná ochrana `dataLen < 2`
   nechávala projít velikosti, u kterých bezpodmínečný zápis 14bajtového klíčového
   slova `"MailLoadTester"` přetekl přes hranici alokovaného pole →
   `IndexOutOfRangeException`. Aktuálně nedosažitelné přes GUI (min. 1 MB), ale
   nášlapná mina pro budoucí změny. Opraveno + regresní test (200 velikostí 1..200 B).

### TemplateTags.cs
Beze změn — čistý, dobře navržený fast-path.

## Fáze 2 — Connection pooling & concurrency

### AdaptiveConcurrencyLimiter.cs
6. **Race condition → trvalý únik permitů.** `Release()`/`RecordAttempt()` volaly
   `TrySetResult(true)` až po odemčení zámku; souběžné zrušení (`ct.Register` →
   `TryCancelWaiter`) mohlo vyhrát závod s `TrySetCanceled()` na stejném
   `TaskCompletionSource`. Pokud vyhrálo zrušení, `AcquireAsync` hodilo
   `OperationCanceledException`, volající (`SmtpTestRunner`) proto nezavolal zpět
   `Release()` — ale `_active` už bylo interně navýšeno. Efektivní paralelismus se
   nevratně snižoval. Opraveno přesunem `TrySetResult`/grantu dovnitř zámku
   (bezpečné díky `RunContinuationsAsynchronously`). Regresní stres test (500 běhů).

### SmartPaceController.cs
7. `GetWarmupPhases()` parsovala `_options.WarmupPhases` (Split+LINQ) znovu při
   každém volání — volá se jednou na zprávu po celou dobu warm-up fáze. Cachováno.

## Fáze 3 — Síťová vrstva a rotace

### IpV4Rotator.cs
8. **Bug (OOM riziko):** limit "max 4096 adres" se kontroloval až PO plné expanzi
   CIDR do `List<IPAddress>`. Překlep `/8` místo `/28` (nebo `/1`) by se pokusil
   vytvořit miliony/miliardy `IPAddress` objektů, než by se limit vůbec uplatnil.
   Opraveno — limit se vynucuje průběžně během expanze. Regresní test ověřuje
   rychlé selhání (`/8` → chyba pod 5s).

### IpV6Rotator.cs
Beze změn — bezpečné, žádná explozivní alokace.

### IpBindingHelper.cs
9. **Bug (source-IP binding nefunguje ve výchozím režimu):** `CreateBoundSocket` pro
   `preference == Any` (výchozí hodnota!) vždy vytvořil `AddressFamily.InterNetworkV6`
   dual-mode socket a pak na něj zkusil `Bind()` čistou IPv4 `IPEndPoint` — .NET to
   bez explicitního IPv4-mapped formátu (`::ffff:x.x.x.x`) odmítá `SocketException`.
   V praxi: kdokoliv nastavil IPv4 `SourceIp` a nechal `IpVersion` na výchozí `Any`,
   dostal chybu na každém pokusu o spojení. Opraveno — rodina socketu se teď vždy
   odvozuje ze skutečné rodiny `localEp`, když je zadaný. Přidány regresní testy.

## Zbývá — Fáze 4 (CircuitBreaker.cs, MxResolver.cs, IpBanDetector.cs)
CircuitBreaker.cs už byl částečně opraven v předchozím kole (`EverOpened`).
MxResolver.cs a IpBanDetector.cs zatím neauditováno v tomto kole.

## Fáze 4 — Ochranné mechanismy a DNS

### CircuitBreaker.cs
Beze změn v tomto kole — `EverOpened` sticky flag byl přidán už v předchozím kole oprav
(viz AUDIT-FIXES-2.9.22.md).

### MxResolver.cs
10. **Bug (falešně permanentní DNS výpadek):** cache neuměla rozlišit "doména
    opravdu nemá MX/A záznam" (potvrzený negativní výsledek) od "dotaz selhal"
    (timeout, nedostupný resolver, zahozený/spoofed paket, nečitelná odpověď) —
    obojí se cachovalo na plných 10 minut. Jeden přechodný DNS výpadek uprostřed
    běhu tak "otrávil" cache a všechny další zprávy na danou doménu selhávaly
    hlášením "nemá žádný MX ani A záznam", dokud cache nevypršela — i když DNS
    bylo zpátky v pořádku během pár vteřin.
    Opraveno: `QueryMxAsync` teď vrací `null` pro nedůvěryhodný/neúspěšný dotaz
    (odlišené od potvrzeně prázdné odpovědi typu NXDOMAIN), `ResolveAsync` cachuje
    potvrzené výsledky na 10 min, ale neúspěšné dotazy jen na 15 s.

### IpBanDetector.cs
Beze změn — triviální, čistá heuristika bez skutečného bugu.

---
**Shrnutí celého hloubkového auditu (Fáze 1–4): 10 reálných nálezů, všechny opraveny.**
