# MailLoadTester 2.9.16 — Maximum Brutal Edition (GUI)

## Co je v balíčku
- **MailLoadTester.Core** — logika SMTP, pool, validace, stavový automat, circuit breaker, adaptive concurrency, MX resolver, dashboard server, bandwidth limiter, webhook, session logger
- **MailLoadTester.Gui** — Windows Forms aplikace s 6 záložkami, tooltipy, workery, checklistem
- **Testy** — xunit testy pro Core
- **Build skripty** — jedním klikem vytvoříš EXE a instalátor

## Rychlý start

### 1. Build self-contained EXE
```
build.bat
```
Výsledek: `publish\MailLoadTester.exe` (~160 MB, funguje bez .NET na cílovém PC)

### 2. Build + Instalátor
```
build-installer.bat
```
Vyžaduje [Inno Setup](https://jrsoftware.org/isdl.php). Výsledek: `installer\Output\Setup-MailLoadTester.exe`

### 3. Použití
1. Spusť `publish\MailLoadTester.exe`
2. Vyplň záložku **SMTP Server** (host, port, zabezpečení, auth metoda, source IP, mTLS, timeouty)
3. Vyplň záložku **Zpráva** (od, komu, cc, bcc, předmět, tělo, HTML, SMTPUTF8, přílohy, inline obrázky, EML šablona)
4. Nastav záložku **Test** (počet zpráv, paralelismus, interval, retry, dávky, dry-run, test mode)
5. Volitelně: záložka **Proxy** (SOCKS5 pro Tor / corporate proxy)
6. Volitelně: záložka **Pokročilé** (Direct MX, Pre-warm, Adaptive concurrency, Circuit breaker, Dashboard, Auto-restart, Screenshot, Session log)
7. Volitelně: záložka **Export / Síť** (JSON/HTML reporty, Webhook, Bandwidth limit, IP verze)
8. Klikni **▶ START**
9. Sleduj log, workery a checklist v dolní části okna

## Všechny funkce v 2.9.16

### Základní
- Windows Forms GUI s tooltipy na každém poli
- Live progress bar, ETA, sent/failed counter
- Panel workerů W1…Wn s barevným stavem
- Checklist zprávy: Fronta → Rate limit → SMTP → MIME → SEND → OK
- Barevný log (posledních 5000 řádků)
- Test SMTP připojení bez odeslání zprávy
- Dry-run režim
- Batch mode s pauzou
- JSON + HTML export reportů

### Bezpečnost & Autentizace
- **SMTP Auth metody**: Auto, Plain, Login, CRAM-MD5, SCRAM-SHA-1, NTLM, OAuth2
- **Client certificate (mTLS)**: vlastní X.509 certifikát při TLS handshake
- **Ignore certificate errors**: pro testování s self-signed certy
- **SOCKS5 proxy**: podpora Tor, corporate proxy s autentizací

### Síť & Routing
- **Source IP binding**: váže SMTP socket na konkrétní lokální IP
- **IP verze**: Any, IPv4Only, IPv6Only, DualStack
- **Direct MX delivery**: DNS MX lookup, přímé spojení s cílovým poštovním serverem
- **Network adapter info**: detekce aktivních adaptérů a jejich stav

### Výkon & Spolehlivost
- **Connection pre-warming**: vytvoří spojení před testem, první dávka nečeká
- **Adaptive concurrency**: při chybách snižuje paralelismus, při stabilitě zvedá
- **Circuit breaker**: po N selháních stejného typu přestane zkoušet (fail-fast)
- **Bandwidth limiter**: omezení rychlosti odesílání v kbps
- **Oddělené timeouty**: connect timeout vs read timeout
- **FIFO connection pool**: spravedlivé reuse spojení

### Zprávy & Formáty
- **EML šablona**: načtení reálné zprávy ze souboru .eml
- **BCC & CC**: skryté a viditelné kopie
- **SMTPUTF8**: Unicode v e-mailových adresách (RFC 6531)
- **Inline attachments**: obrázky vložené do HTML přes CID reference
- **Vlastní X- hlavičky**: libovolné hlavičky s validací
- **Náhodná testovací data**: generování variabilního obsahu

### Monitoring & Debugging
- **Real-time HTTP dashboard**: mini webový server na localhost, live grafy v prohlížeči
- **SMTP session log**: kompletní SMTP konverzace do .txt (Wireshark-style)
- **Screenshot při chybě**: automatický PNG screenshot GUI při pádu
- **Webhook notifikace**: POST JSON výsledku na URL (Slack, Teams, CI/CD)

## Audit oprav (z původního kódu)
| Chyba | Oprava |
|-------|--------|
| ConcurrentBag → náhodné reuse | ConcurrentQueue (FIFO) |
| Synchronní SafeDispose blokuje thread pool | SafeDisposeAsync s await |
| _disposed race condition | Kontrola hned po WaitAsync |
| _created++ před úspěchem | Increment až po _all.TryAdd |
| DateTime.UtcNow v RateLimiter (~15ms) | Stopwatch.GetTimestamp() |
| Phantom delay při cancel | Fronta rezervací Queue<long> |
| activeTicks += není thread-safe | Interlocked.Add |
| EstimateEta používá DateTime.UtcNow | Stopwatch.Elapsed |
| BuildMessage čte přílohy sync do RAM | MimeContent(File.OpenRead) |
| Subject limit 998 znaků (ne bytů) | Limit 200 znaků |
| ParseHeaders přeskakuje chyby | throw ArgumentException |
| TryGetDefaultInterfaceIndex nepřesný | Lepší heuristika |
| AttachmentPlanner WorkingSet64*4 | Fallback na 2GB / GC info |
| Chybí ConfigureAwait(false) | Přidáno do celého Core |

## Testy
```
dotnet test
```

## Poznámky
- Self-contained EXE je ~160 MB (obsahuje .NET runtime)
- Na cílovém PC není potřeba nic instalovat
- Dashboard vyžaduš práva pro HTTP listener (první spuštění může vyžadovat admin práva)
- SOCKS5 proxy podporuje autentizaci username/password
- mTLS vyžaduš .pfx soubor s klientským certifikátem


## 2.9.16 — hluboký audit a opravy
- Health-check idle SMTP spojení přes SMTP NOOP před znovupoužitím dlouho nečinného spojení.
- Přesné odstranění zrušené rezervace z globálního RateLimiteru bez phantom delay.
- Integrační test health-checku connection poolu.
