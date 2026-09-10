# Doporučení pro další kolo hloubkového auditu

Tohle NENÍ seznam potvrzených bugů — je to mapa **systémových vzorů**, které se
opakují napříč více soubory a doteď byly auditovány jen bodově (soubor po
souboru), ne napříč celou codebase. U každého bodu je uvedeno, kde přesně se
vzor vyskytuje, aby další kolo (klidně i jiná AI) nemuselo znovu procházet
všechno od nuly.

Metoda: statické greppování + ruční inspekce nálezů. Bez `dotnet build`/`test`
(SDK není v tomto prostředí k dispozici) — priorita #0 zůstává to, co
doporučila i druhá AI: spustit `dotnet test` a `dotnet build -c Release` u vás
lokálně, než se řeší cokoliv dalšího níž.

---

## 1. Tiché `catch { }` bloky — projít všechny, ne jen ty nápadné

Aktuální stav (12 výskytů v `MailLoadTester.Core`):

| Soubor:řádek | Kontext | Riziko |
|---|---|---|
| `AttachmentPlanner.cs:203,210` | (nezjišťováno detailně) | zkontrolovat, co konkrétně polykají |
| `DashboardServer.cs:47,77` | HTTP listener loop | pravděpodobně OK (`ObjectDisposedException` po Stop()) — ověřit, že nepolyká i skutečné chyby zápisu odpovědi |
| `Models.cs:156` | `catch { return false; }` — validace | OK vzor, ale ověřit že nezakrývá např. chybu v regexu, kterou by chtěl vývojář vidět |
| `MxResolver.cs:126` | fallback DNS server → `1.1.1.1` | v pořádku, jen ověřit že se nepoužije `1.1.1.1` tiše i tam, kde uživatel explicitně nastavil jiný resolver a očekává chybu při jeho nedostupnosti |
| `NetworkAdapterInfo.cs:128,144,193` | enumerace síťových adaptérů | typicky OK (defenzivní), ale 3 výskyty v jednom souboru — stojí za sjednocení do jedné pomocné metody s jasnou politikou |
| `SmtpConnectionPool.cs:116,366,368` | health-check / cleanup | **nejcitlivější místo** — `catch { Forget(idleClient); }` na řádku 116 polyká úplně všechno včetně např. `OutOfMemoryException`; stálo by za to alespoň logovat co se stalo přes `_sessionLogger` místo tichého zahození |
| `SmtpSessionLogger.cs:52` | pravděpodobně I/O logu | ověřit, že selhání zápisu logu nezpůsobí, že se ztratí i důvod selhání testu |
| `SmtpTestRunner.cs:626` | (kontext neověřen v tomto kole) | zkontrolovat |
| `WebhookNotifier.cs:38` | zdokumentované "best effort" | OK, má komentář vysvětlující proč |

**Doporučení do dalšího kola:** projít každý z nich a u těch bez komentáře
přidat buď (a) krátký komentář proč je bezpečné chybu zahodit, nebo (b) aspoň
`_sessionLogger?.LogWarning(...)` místo úplného ticha — hlavně u
`SmtpConnectionPool.cs:116`, kde tichá chyba při health-checku může maskovat
systémový problém (např. že se najednou *všechny* connection health-checky
začaly hroutit kvůli nesouvisející příčině, a pool to jen potichu "Forget"uje
jednu connection za druhou bez jakékoli stopy v logu).

---

## 2. Kredenciály v protokolovém logu — ověřit end-to-end, ne jen že property existuje

`ProtocolPathObserver.AuthenticationSecretDetector` a `SmtpSessionLogger.AuthenticationSecretDetector`
jsou obě jen **properties** — nikde v kódu se nepřiřazuje konkrétní instance
detektoru (`grep "AuthenticationSecretDetector ="` ukáže jen kopírování mezi
`_a`/`_b` v kompozitním observeru). Komentář v `SmtpConnectionPool.cs:165`
tvrdí, že si ho MailKit "sám napojí" v konstruktoru `SmtpClient` při
autentizaci — což **je** zdokumentované chování MailKitu, takže to
pravděpodobně není bug. Ale:

- Není to nikde **testováno** — chybí test, který by reálně ověřil, že heslo
  z `AUTH LOGIN`/`AUTH PLAIN` se do `SmtpSessionLogger`/GUI logu skutečně
  nedostane v čitelné podobě.
- Stojí za ověření i pro `DIRECT_MX` / vlastní SMTP servery bez STARTTLS, kde
  se AUTH PLAIN posílá v base64 bez šifrování — detektor musí fungovat i tady.

**Doporučení:** napsat jeden integrační test (lokální fake SMTP server, který
vyžaduje AUTH LOGIN) a ověřit, že výstup `SmtpSessionLogger`/`ProtocolPathObserver`
neobsahuje heslo v žádné podobě (ani base64).

---

## 3. `SmtpConnectionPool` a `ProtocolPathObserver` — souhrnná revize souběhu

Tohle kolo řešilo dvě konkrétní races (dispose-race v poolu, pending-step
race v observeru). Doporučuji **jeden systematický průchod** přes celý
`SmtpConnectionPool.cs` (399 řádků) hledající všechna místa, kde:
- se čte pole, pak `await`, pak se to samé pole znovu použije bez re-check
  (accountig/actual-state assumptions po async mezeře),
- se něco vkládá/odebírá ze `ConcurrentDictionary`/`ConcurrentQueue` ve více
  krocích, které nejsou atomické jako celek.

Toto je přesně vzor, který způsobil dispose-race bug — pravděpodobnost, že
existuje ještě 1–2 podobná místa, je reálná u souboru téhle velikosti a
komplexity.

---

## 4. `MainForm.cs` (GUI, 1399 řádků) — dosud vůbec neauditováno

Celý dosavadní hloubkový audit (fáze 1–4) se soustředil na `Core`. GUI vrstva
zůstala stranou. Konkrétní rizika k prověření:
- **UI thread marshaling** — žádný `async void` nebyl nalezen (dobré), ale
  stojí za to zkontrolovat, že všechny `Task`-vracející event handlery mají
  patřičné `try/catch` (bez `async void` totiž nezachycená výjimka v
  `async Task` handleru skončí jako "unobserved task exception", ne jako pád
  UI — ale i tak stojí za ověření, že se chyba aspoň zaloguje/zobrazí).
- **`IProgress<T>` → GUI update frekvence** — `SmtpTestRunner.Report()` má
  throttling (`MinReportIntervalMs = 125`), ale stojí za ověření, že GUI
  handler samotný neserializuje/neblokuje na UI vlákně u velmi rychlých testů.
- **Zavírání formuláře uprostřed běžícího testu** — co se stane s
  `CancellationTokenSource`/pool/dashboard, když uživatel zavře okno dřív, než
  test doběhne? (`FormClosing` handler — ověřit, že korektně čeká/zruší, ne že
  nechá poolu/serveru viset na pozadí.)

---

## 5. Konzistence formátování čísel/dat (kultura)

Grep na `double.Parse`/`int.Parse`/`ToLower()`/`ToUpper()` bez explicitní
kultury nenašel nic — což je dobrá zpráva (buď se nepoužívá, nebo se všude
používá `CultureInfo.InvariantCulture`/ordinal varianty). **Není nutné řešit**,
jen zaznamenávám jako ověřenou položku, aby to příští audit nekontroloval znovu.

---

## 6. `Random`/`Random.Shared` — thread-safety

Grep na `new Random(` nenašel žádný výskyt — vše zjevně jede přes
`Random.Shared` (thread-safe od .NET 6+). **Ověřeno, bez nálezu.**

---

## 7. Validace uživatelských cest k souborům (path traversal / symlink)

Nebylo v tomto kole zkoumáno vůbec: `EmlTemplateParser`, `AttachmentPlanner`,
inline attachments — všechny berou cestu k souboru přímo od uživatele
(GUI file picker i CLI). U desktopové aplikace, kde uživatel sám vybírá
soubory, to není bezpečnostní bug v klasickém slova smyslu (uživatel má
přístup ke svému souborovému systému stejně), ale stojí za krátkou kontrolu:
- Co se stane, když cesta ukazuje na symlink/junction mimo očekávaný adresář?
- Co se stane s UNC cestami (`\\server\share\file`) na Windows — respektuje
  je `AttachmentPlanner`/`EmlTemplateParser` stejně jako lokální cesty, nebo
  tam může být rozdíl v `FileInfo.Length`/přístupových právech, který se
  projeví až za běhu testu (uprostřed odesílání) místo při validaci?

---

## 8. `CircuitBreaker`, `RateLimiter`, `AdaptiveConcurrencyLimiter` — sjednocený model souběhu

Všechny tři řeší podobný problém (koordinace mezi workery) různým způsobem
(`ConcurrentDictionary` + `lock`, čistý `lock`, `lock` + `TaskCompletionSource`
fronta). Není to bug, ale stojí za zvážení, jestli by extra kolo auditu nemělo
projít všechny tři **společně** a hledat konkrétně vzor, který už jednou
způsobil bug v `AdaptiveConcurrencyLimiter` (grant/cancel race) — tedy jestli
`RateLimiter`/`CircuitBreaker` nemají analogické místo, kde se
"dokončení"/"zrušení" nějakého stavu řeší mimo zámek.

---

## Doporučené pořadí příštího kola

1. `dotnet test` + `dotnet build -c Release` (blokující pro cokoliv dalšího).
2. Bod 3 (systematický průchod `SmtpConnectionPool.cs` na další races).
3. Bod 1 (tiché `catch{}` — hlavně `SmtpConnectionPool.cs:116`).
4. Bod 2 (test na únik hesla do logu).
5. Bod 4 (GUI vrstva — zatím netknutá).
6. Body 7–8 podle chuti/času — nižší riziko, spíš hygiena než aktivní bug.
