# MailGuardian — audit stavu

> **Datum:** 2026-09-10  
> **Rozsah:** MailGuardian (Python)  
> **Poznámka:** Tento audit je založen na skutečném projektu dodaném v `MailGuardian.zip` a na ověřených artefaktech dostupných v repozitáři. Položky označené jako `dohledat` nesmí být považovány za implementované jen podle názvu souboru.

## 0. Přehled

MailGuardian je samostatná Python aplikace pro lokální analýzu a forenzní zpracování e-mailů/EML. Obsahuje CLI, Streamlit UI, SQLite persistence, analytické moduly, externí enrichment a testy/CI.

MailGuardian **není SMTP sending engine**. Odesílání patří do samostatné aplikace `MailSenderEngine`.

### Známé komponenty

- `main.py` — CLI entry point
- `app.py` — Streamlit UI
- `modules/parser.py` — EML parsing
- `modules/fingerprint.py` — fingerprinting
- `modules/similarity.py` — podobnost zpráv
- `modules/threat.py` — threat/IOC logika
- `modules/confidence.py` — confidence/scoring
- `modules/evidence.py` — evidence
- `modules/case.py` — case management
- `modules/database.py` — SQLite persistence
- `modules/gmail_connector.py` — Gmail integrace
- `modules/dns_lookup.py`, `whois_lookup.py` — enrichment
- `modules/virustotal.py`, `abuseipdb.py` — externí threat enrichment
- `tests/` — testovací sada
- `.github/workflows/ci.yml` — CI

## 1. Parser e-mailů

**Stav: 🟡 částečně / vyžaduje dokončení auditu**

Parser je samostatný modul a projekt pracuje s EML zprávami. Přesný rozsah podpory všech MIME edge-case scénářů musí být ověřen testy a zdrojovým kódem.

Je nutné explicitně ověřit:

- malformed/truncated EML;
- nested multipart;
- inline attachments;
- charset/quoted-printable/base64 edge cases;
- chování při chybějící hlavičce;
- zachování originálních hodnot potřebných pro forensic evidence.

## 2. Fingerprinting

**Stav: 🟡 implementováno, vyžaduje validační audit**

Projekt obsahuje samostatný fingerprint modul. Fingerprint musí být hodnocen podle determinismu, normalizace a rizika kolizí/false-positive.

Nutno ověřit konkrétní vstupy fingerprintu, hashovací algoritmy a přesné ukládání výsledků do persistence vrstvy.

## 3. Similarity / O(n²)

**Stav: ⚠️ implementováno, známé potenciální škálovací riziko**

Projekt obsahuje similarity analýzu a režim compare-all. Při porovnání každého páru zpráv může vzniknout kvadratická složitost:

`N * (N - 1) / 2`

To je přijatelné pro menší dataset, ale není vhodné předpokládat škálování na statisíce/miliony zpráv bez candidate selection/indexace.

Před optimalizací je potřeba benchmark skutečného bottlenecku.

## 4. IOC

**Stav: 🟡 implementováno, vyžaduje detailní validační audit**

Threat/analytické moduly pracují s indikátory a externím enrichmentem. Nutno ověřit přesný rozsah IOC:

- URL;
- IP;
- domény;
- e-mailové adresy;
- attachment/file hashes;
- hlavičkové indikátory;
- normalizace a deduplikace;
- exportní formáty.

Externí služby nesmí být podmínkou pro základní offline analýzu.

## 5. Evidence / chain of custody

**Stav: 🟡 implementováno, vyžaduje integrity audit**

Projekt obsahuje evidence/case komponenty. Je potřeba ověřit, zda každý relevantní artefakt obsahuje dostatečnou provenance, hash, timestamp a zdroj a zda je možné rekonstruovat změny.

Forensic data se nesmí při normalizaci tiše ztratit. Originální hodnota musí zůstat dostupná tam, kde je součástí důkazního řetězce.

## 6. SQLite / persistence

**Stav: 🟢 implementováno; hardening k ověření**

Projekt používá SQLite persistence přes `modules/database.py`.

Je nutné ověřit:

- skutečné schéma;
- indexy podle query patternů;
- transaction boundaries;
- WAL/concurrency režim;
- recovery po přerušení;
- schema versioning/migrations;
- očekávanou velikost databáze.

## 7. Gmail connector

**Stav: 🟡 implementováno; security/recovery audit nutný**

Projekt obsahuje `modules/gmail_connector.py`.

Je nutné ověřit:

- OAuth flow;
- scopes;
- token persistence;
- revocation/expired token handling;
- incremental synchronization;
- quota/rate-limit handling;
- deduplikaci;
- recovery po přerušení synchronizace.

Credentials/tokeny nesmí být součástí repozitáře.

## 8. Bezpečnost

**Stav: 🟡**

Architektura podporuje lokální forensic práci, ale security audit musí zahrnout externí API credentials, Gmail OAuth tokeny, logy, exportované evidence, PII a bezpečné zacházení s přílohami.

HTML/EML obsah nesmí být automaticky považován za důvěryhodný obsah. UI/rendering musí být oddělen od aktivního vykonávání obsahu.

Dependency vulnerability audit je nutné provádět v CI nebo release procesu.

## 9. Testy + CI

**Stav: 🟢 existují testy a CI; coverage/detail nutno ověřit**

Projekt obsahuje `tests/` a GitHub Actions CI. Audit má potvrdit počet testů, Python matrix, lint/type-check a skutečné coverage.

Prioritní regression fixtures: malformed EML, multipart, authentication headers, attachment metadata, similarity thresholds a evidence integrity.

## 10. Výkon, mrtvý kód, dokumentace

**Stav: 🟡**

Největší známé riziko je quadratic compare-all. Je potřeba benchmark a profilování před změnou algoritmu.

Dále je nutné prověřit mrtvé moduly, duplicitní generace kódu a shodu README s aktuálním chováním.

## Shrnutí

### ✅ Funguje a zůstává

- Python MailGuardian jako samostatný forensic/OSINT program.
- Modulární parser/analysis architektura.
- SQLite persistence.
- CLI + Streamlit UI.
- Evidence/Case koncept.
- Testovací sada a CI.
- Volitelné externí enrichment služby.

### ⚠️ Refaktorovat / hardenovat

- MIME/error edge cases.
- Fingerprint determinism/validation.
- Similarity scalability.
- Evidence integrity/provenance.
- SQLite indexing/concurrency/versioning.
- Gmail OAuth/sync recovery.
- Security/privacy/logging.
- Dependency and coverage verification.

### ❌ Nedoplňovat do MailGuardian

- SMTP sending workers.
- SMTP session pool.
- SMTP retry engine.
- Sending rate limiter.
- Campaign scheduler.
- Delivery queue.

Tyto funkce patří do `MailSenderEngine`.

## Rozhodnutí o architektuře

```text
mail-guardian/
├── MailGuardian/        # Python — analysis/forensics
└── MailSenderEngine/    # C#/.NET — sending/delivery
```

Aplikace mohou později sdílet pouze explicitně definovaný kontrakt (např. stabilní MessageId/CampaignId/CorrelationId nebo API/event boundary). Nemají sdílet implementaci business logiky.

## Návrh dalšího kroku

1. Dokončit detailní source-level ověření sekcí 1–10 a přidat konkrétní soubory/testy/metry.
2. Opravit pouze potvrzené security, correctness a scalability problémy.
3. Pokračovat ve vývoji `MailSenderEngine` vertikálním řezem: jedna zpráva → persistentní SMTP session → následně worker/queue/retry/rate-limit.

## Co teď nedělat

- nepřepisovat MailGuardian do C#;
- nepřidávat SMTP do Python analyzátoru;
- neoptimalizovat similarity bez benchmarku;
- nestavět deset prázdných architektonických vrstev v MailSenderEngine bez funkčního use-case řezu;
- nepřidávat tracking/evasion mechanismy bez explicitního legitimního požadavku a privacy review.
