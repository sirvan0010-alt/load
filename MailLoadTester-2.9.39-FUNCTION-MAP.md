# MailLoadTester 2.9.39 — funkční a architektonický audit

## 1. Rozsah auditu

- Primární baseline: MailLoadTester 2.9.39 FINAL.
- Audit zahrnuje Core, GUI, testy, build/installer a auditní dokumentaci.
- Stávající funkce (včetně IP/proxy rotace) jsou v baseline zachovány; žádná funkce se tímto auditem nemaže.
- Stav je rozdělen na KEEP / IMPROVE / REFACTOR / DEPRECATE / REMOVE. REMOVE vyžaduje samostatné odůvodnění a schválení.

## 2. Celková struktura

```text
MailLoadTester.sln
├── src/MailLoadTester.Core          # doménová, síťová a řídicí vrstva
├── src/MailLoadTester.Gui           # WinForms UI a orchestrace UI
├── tests/MailLoadTester.Tests       # jednotkové/regresní testy
├── installer/                        # Inno Setup
├── build*.bat                        # build/release pomocné skripty
└── AUDIT*/README*                    # vývojová a auditní historie
```

## 3. Funkční oblasti

| Oblast | Komponenty | Úloha |
|---|---|---|
| Konfigurace | Models, ProfileStore, ProviderPresets | konfigurace testu, profily, preset konfigurace |
| SMTP transport | SmtpTestRunner, SmtpConnectionPool, SmtpConnectivityTester | připojení, SMTP session, odeslání, reuse, health-check |
| SMTP auth/TLS | AuthMethodHelper, ClientCertificateHelper | SASL metody, klientské certifikáty, TLS |
| Tempo | RateLimiter, SmartPaceController | interval, jitter, burst, backoff, warmup, časová okna |
| Adaptace | AdaptiveConcurrencyLimiter | adaptivní počet souběžných operací |
| Ochrana | CircuitBreaker, BandwidthLimiter | ochrana při chybách a omezení datového toku |
| Síťová cesta | IpBindingHelper, IpV4Rotator, IpV6Rotator, ProxyClientFactory, ProxyRotator | source IP, IPv4/IPv6 pool, proxy konfigurace a rotace |
| DNS/preflight | MxResolver, DnsPolicyChecker, RblChecker, IpBanDetector | MX, SPF/DMARC, RBL a detekce blokace |
| Payload | TemplateTags, RandomTestData, AttachmentPlanner, EmlTemplateParser | dynamický obsah, HTML, přílohy, EML |
| Observability | DeliveryPath, ProtocolPathObserver, SmtpSessionLogger, ObservedResponses | SMTP cesta, protokol, session logy, klasifikace odpovědí |
| Integrace | WebhookNotifier, DashboardServer | webhook a lokální dashboard |
| GUI | MainForm, SmtpLogTab | ovládání, progress, log, konfigurace |
| Stav | TestStateMachine, ProgressUpdate | lifecycle a progress události |

## 4. Řídicí tok aplikace

```text
MainForm
  │ BuildOptions / validation / profile / preset
  ▼
SmtpTestRunner.RunAsync
  │
  ├─ TestStateMachine             stav SMTP workflow
  ├─ SmtpConnectionPool            persistentní/reuse SMTP klienti
  │    ├─ ProxyClientFactory/ProxyRotator
  │    ├─ IpBindingHelper
  │    └─ ClientCertificateHelper
  ├─ MxResolver / DeliveryPath      DNS/MX + sledování SMTP kroků
  ├─ RateLimiter                    základní tempo
  ├─ SmartPaceController            jitter/burst/backoff/recipient/time-window/warmup
  ├─ AdaptiveConcurrencyLimiter     adaptivní paralelismus
  ├─ CircuitBreaker                 ochrana při chybách
  ├─ BandwidthLimiter               omezení přenosu
  ├─ TemplateTags / EmlTemplateParser / RandomTestData / AttachmentPlanner
  │                                  generování MIME payloadu
  ├─ ObservedResponses               klasifikace odpovědí/knowledge base
  ├─ SmtpSessionLogger / ProtocolPathObserver
  │                                  diagnostika SMTP protokolu
  ├─ WebhookNotifier                externí notifikace
  └─ DashboardServer                lokální monitoring

RunAsync → Report/ProgressUpdate → MainForm.UpdateProgress + SmtpLogTab
```

## 5. Automatizace

### Start testu

1. GUI sestaví `MailTestOptions`.
2. Validace kontroluje adresy, SMTP, porty, TLS, concurrency, retry, cesty, hlavičky a další limity.
3. Volitelně se provede RBL check, MX lookup, předhřátí poolu a inicializace dashboardu.
4. Runner spustí stavový SMTP workflow.
5. Pool pronajímá/recykluje spojení; proxy/source-IP komponenty určují síťovou cestu podle konfigurace.
6. Tempo je řízeno kombinací RateLimiter + SmartPaceController; podle volby může AdaptiveConcurrencyLimiter měnit concurrency.
7. Chyby procházejí klasifikací, retry/backoff a CircuitBreaker.
8. Každá zpráva aktualizuje počitadla, latence, throughput, progress a delivery path.
9. Na konci se spojení bezpečně vrátí/discardují, session log flushne, webhook/dashboard dostanou finální stav.

### Obsah zprávy

`TemplateTags` → `RandomTestData` / `AttachmentPlanner` / `EmlTemplateParser` → `SmtpTestRunner.BuildMessage` → SMTP DATA. Generování payloadu je oddělené od transportní vrstvy na úrovni jednotlivých komponent.

### Automatické tempo

- `RateLimiter`: minimální interval mezi akcemi.
- `SmartPaceController`: globální a recipient okna, jitter, burst, progressive backoff, sending time window, warmup a greylist retry.
- `AdaptiveConcurrencyLimiter`: sleduje výsledky a vyhodnocuje okno pro změnu concurrency.
- `CircuitBreaker`: consecutive/sliding-window ochrana.
- `BandwidthLimiter`: omezení přenosu.

## 6. Zachování existujících funkcí

IP binding, IPv4/IPv6 rotace, proxy list/rotace, MX delivery, pre-warm, adaptive concurrency, circuit breaker, dashboard, session log, webhook, random data, attachments, pacing profily, warmup, greylist, RBL a observed responses jsou součástí baseline a budou nejprve auditovány, nikoli automaticky odstraněny.

## 7. Auditní klasifikace

| Stav | Význam |
|---|---|
| KEEP | funkce i implementace jsou v pořádku |
| IMPROVE | funkci zachovat, zlepšit bezpečnost/spolehlivost/testy |
| REFACTOR | zachovat chování, změnit vnitřní architekturu |
| DEPRECATE | pouze pokud audit prokáže problém a existuje náhrada |
| REMOVE | pouze po technickém odůvodnění a explicitním rozhodnutí |

## 8. Další auditní priority

| Priorita | Oblast | Akce |
|---|---|---|
| P0 | cancellation/disposal | ověřit všechny async cesty a lifetime |
| P0 | SMTP retry | oddělit bezpečné transient retry od potenciálně duplicitního SEND |
| P0 | secrets | ověřit profily/logy a citlivé údaje |
| P1 | orchestrace | zmenšit vazbu GUI → Runner a formalizovat pipeline |
| P1 | tempo | sjednotit odpovědnosti RateLimiter/SmartPace/AdaptiveConcurrency |
| P1 | observability | sjednotit event model pro GUI/dashboard/log/webhook |
| P1 | testy | doplnit integrační testy s fake SMTP serverem a cancellation/disposal scénáři |
| P2 | API/DI | zvážit rozhraní pro payload, transport, pacing a telemetry bez změny chování |
