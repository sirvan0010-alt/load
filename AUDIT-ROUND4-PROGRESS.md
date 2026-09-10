# Deep Audit Round 4 — stav rozpracovanosti

Podle plánu z předchozí zprávy (10 bodů). Co je hotovo v tomto kole, co ne —
u nedokončených bodů explicitně NEOVĚŘENO, ne že "je to v pořádku".

## Hotovo

### 1. SmtpConnectionPool.cs — kompletní průchod
Prošel jsem RentAsync (idle-reconnect, health-check, nová connection),
Return/Discard (idempotence, double-return), PreWarmAsync, DisposeAsync.
**Ověřeno, žádný nový bug:** obava z "DisposeAsync race s aktivně používaným
leased klientem" se nepotvrdila — `SmtpTestRunner` vždy čeká na
`Task.WhenAll(tasks)` (které per definici nevrátí řízení, dokud nejsou
VŠECHNY tasky dokončené, ať už úspěchem, chybou i zrušením) předtím, než
`finally` zavolá `pool.DisposeAsync()`. Nebyl nalezen scénář, kde by
DisposeAsync běžel souběžně s worker vláknem aktivně používajícím leased
klienta.
**Oprava:** `catch{}` na řádku 116 (idle reconnect failure) teď loguje
příčinu do session logu místo tichého zahození.

### 2. Všech 12 catch{} — klasifikováno
- `SmtpConnectionPool.cs:116` → opraveno (log místo ticha).
- `DashboardServer.cs:47,77` → zdokumentováno komentářem (shutdown /
  best-effort), beze změny chování.
- `SmtpSessionLogger.cs:52` → **skutečná oprava.** Buffer se mazal PŘED
  zápisem na disk, takže při selhání zápisu (plný disk, zamčený soubor)
  se diagnostický obsah nenávratně ztratil beze stopy. Teď se při selhání
  vrací zpět do bufferu (retry při dalším tiku timeru), s bezpečnostním
  stropem na velikost bufferu (4M znaků) proti neomezenému růstu při
  trvalé poruše disku.
- `MxResolver.cs:126`, `Models.cs:156`, `AttachmentPlanner.cs:203,210`,
  `NetworkAdapterInfo.cs:128,144,193`, `SmtpTestRunner.cs:626`,
  `WebhookNotifier.cs:38` → ověřeno, žádná oprava potřeba (buď už
  zdokumentované "best effort", nebo prokazatelně bezpečné díky
  `Task.WhenAll` sémantice, nebo čistě kosmetické UI info bez dopadu na
  korektnost testu).

### 4. MainForm.cs lifecycle — nalezen a opraven přesně predikovaný bug
**Potvrzeno:** `MainForm.cs` neměl žádný `FormClosing` handler. Zavření
okna během běžícího testu:
- nezrušilo `CancellationTokenSource` → běh pokračoval na pozadí i po
  zavření okna,
- každé další `UpdateProgress`/`AppendLog` volání z `IProgress<T>.Report()`
  skončilo výjimkou (`Invoke` na torn-down handle), kterou vnitřní
  catch-all v `SmtpTestRunner`'s per-message smyčce vyhodnotil jako
  **selhání zprávy** — takže zbytek běhu by potichu "failoval" každou
  zprávu z nesouvisejícího důvodu, spotřeboval retry pokusy a běžel dál na
  pozadí až do vyčerpání `MessageCount`, aniž by se kdy skutečně zastavil.

**Oprava:** přidán `FormClosing` handler, který při běžícím testu zavření
odloží (`e.Cancel = true`), zavolá `_cts.Cancel()`, počká na dokončení
běhu (přes nový `TaskCompletionSource` signalizovaný z `finally` bloku
`OnStartAsync`) a teprve pak okno skutečně zavře. Navíc `UpdateProgress`/
`AppendLog` mají defenzivní `IsDisposed` kontrolu a odchytávají
`ObjectDisposedException`/`InvalidOperationException` z `Invoke` jako
pojistku pro těsné časové okno.

### 6. Integer/size overflow — ověřeno, žádný nález
`AttachmentPlanner.EstimateRandomAttachments` už používá `checked()` a
`Math.Clamp` na všech vstupech (count 1–5, concurrency 1–20) — i při
extrémním `requestedSizeMb` (int.MaxValue) zůstává výpočet v bezpečných
mezích `long`. Grep na klasický vzor `int*int přiřazený do long` (ztráta
přesnosti před promocí) nenašel nic nikde v `Core`.

## NEOVĚŘENO (nestihnuto v tomto kole — nejde o "PASS", jen o "zatím
nekontrolováno")

- **Bod 3 — AUTH secret redaction end-to-end test.** Property
  `AuthenticationSecretDetector` existuje a je zapojená (viz komentář v
  `SmtpConnectionPool.cs:163-168`), ale skutečný integrační test (fake SMTP
  server vyžadující AUTH LOGIN → ověření, že heslo/base64 payload nikde
  neskončí v logu) nebyl napsán. **Bezpečnostně nejcitlivější zbývající
  položka, doporučuji jako první příště.**
- **Bod 5 — Path traversal / UNC / symlink** u uživatelských cest
  (EML šablona, přílohy, inline přílohy, profily, export, webhook) —
  nekontrolováno vůbec.
- **Bod 7 — Modelový audit interakce CircuitBreaker ↔ RateLimiter ↔
  SmartPace ↔ AdaptiveConcurrency ↔ PerRecipientLimiter ↔ ConnectionPool**
  jako celku (ne po jednotlivých třídách) — nekontrolováno.
- **Bod 8 — Cancellation audit celého Core** (seznam všech `await` a
  ověření správného předání `ct`) — nekontrolováno systematicky, jen
  bodově v rámci bodů 1 a 4.
- **Bod 9/10 — `dotnet test` / `dotnet build -c Release`** — pořád nutné
  udělat u vás, v tomto prostředí není .NET SDK.

## Metoda ověřování v tomto kole
Čtení zdrojového kódu + grep, bez spuštění. U bodu 1 a 6 jsem ověřování
doplnil o explicitní trasování sémantiky (`Task.WhenAll` completion
guarantee, `checked()`/`Math.Clamp` rozsahy) — tj. nejde o pouhé "vypadá to
v pořádku", ale o odvození, proč konkrétní scénář není dosažitelný. U bodu
4 jde o reálně reprodukovatelný scénář (chybějící handler je fakt, dopad
je odvozen z kódu, ne domněnka).
