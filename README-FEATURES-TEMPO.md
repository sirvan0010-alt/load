# MailLoadTester 2.9.11 — Tempo, ochrana, handshake a cesta odesílání

Tento dokument je **závazná specifikace** pro implementaci a pro tvorbu GUI.
Každá položka musí být v GUI klikací / konfigurovatelná a informační texty musí
uživateli jasně vysvětlovat, proč dané nastavení existuje.

Verze: 2.9.11  
Cíl: profesionální load-test s ochranou proti blokacím, viditelným handshake
a diagnostikou cesty zprávy.

---

## 1. Nová záložka GUI: „Tempo a ochrana“

Záložka obsahuje tyto sekce (shora dolů):

### 1.1 Režim tempa (přepínač)
- **Agresivní** — minimální pauzy, vysoký paralelismus (pouze pro interní servery)
- **Standardní** — výchozí interval + mírný jitter
- **Šetrný** — delší pauzy, burst+pause, progressive backoff
- **Vlastní** — všechna pole níže jsou editovatelná

Při změně režimu se automaticky vyplní doporučené hodnoty do polí.
Uživatel může kdykoli přepnout na „Vlastní“ a upravit cokoliv.

### 1.2 Základní interval a jitter
| Ovládací prvek | Typ | Rozsah | Výchozí | Tooltip |
|----------------|-----|--------|---------|---------|
| Interval mezi zprávami | NumericUpDown (ms) | 0–3 600 000 | 1000 | Základní pauza mezi odesláním zpráv |
| Jitter (± %) | NumericUpDown | 0–50 | 15 | Náhodná odchylka intervalu. 15 % při 1000 ms = 850–1150 ms |
| Zapnout jitter | CheckBox | – | true | Doporučeno zapnout – snižuje detekci robotického vzoru |

### 1.3 Burst + pause
| Ovládací prvek | Typ | Rozsah | Výchozí | Tooltip |
|----------------|-----|--------|---------|---------|
| Zapnout burst režim | CheckBox | – | false | Po N zprávách následuje delší pauza |
| Velikost burstu | NumericUpDown | 1–100 | 10 | Počet zpráv v jedné dávce |
| Pauza po burstu (s) | NumericUpDown | 1–3600 | 30 | Délka pauzy po dokončení burstu |

### 1.4 Progressive backoff
| Ovládací prvek | Typ | Rozsah | Výchozí | Tooltip |
|----------------|-----|--------|---------|---------|
| Zapnout progressive backoff | CheckBox | – | true | Po chybách 4xx/5xx nebo po N úspěšných zprávách zpomalovat |
| Počet úspěchů do zpomalení | NumericUpDown | 5–1000 | 50 | Po kolika úspěšných zprávách se interval prodlouží |
| Násobitel intervalu | NumericUpDown (0.1) | 1.1–5.0 | 1.5 | Interval se vynásobí tímto číslem |
| Max. interval (ms) | NumericUpDown | 1000–3 600 000 | 60 000 | Strop, kam až může interval narůst |

### 1.5 Per-recipient limit
| Ovládací prvek | Typ | Rozsah | Výchozí | Tooltip |
|----------------|-----|--------|---------|---------|
| Zapnout limit na příjemce | CheckBox | – | false | Max. X zpráv na jednu adresu za časové okno |
| Max. zpráv na příjemce | NumericUpDown | 1–1000 | 20 | |
| Okno (minuty) | NumericUpDown | 1–1440 | 60 | |

### 1.6 Časové okno odesílání
| Ovládací prvek | Typ | Rozsah | Výchozí | Tooltip |
|----------------|-----|--------|---------|---------|
| Omezit na denní dobu | CheckBox | – | false | Odesílat jen v zadaném intervalu hodin |
| Od hodiny | NumericUpDown | 0–23 | 8 | |
| Do hodiny | NumericUpDown | 0–23 | 18 | Mimo toto okno se test pozastaví a čeká |

### 1.7 Warm-up plán
| Ovládací prvek | Typ | Rozsah | Výchozí | Tooltip |
|----------------|-----|--------|---------|---------|
| Zapnout warm-up | CheckBox | – | false | Postupné zvyšování rychlosti |
| Počet fází | NumericUpDown | 2–10 | 3 | |
| Textový popis fází | Multiline / DataGrid | – | 50;150;500 | Počet zpráv v jednotlivých fázích (oddělené středníkem) |

### 1.8 Greylisting a reakce na 4xx
| Ovládací prvek | Typ | Rozsah | Výchozí | Tooltip |
|----------------|-----|--------|---------|---------|
| Detekovat greylist | CheckBox | – | true | 451/452/4xx s textem „try again“ / „greylist“ |
| Odložený retry (minuty) | NumericUpDown | 1–60 | 10 | Po greylistu počkat a zkusit znovu |
| Max. greylist retry | NumericUpDown | 0–10 | 3 | |

### 1.9 Informační panel (živý)
- Aktuální efektivní interval (po jitteru a backoffu)
- Počet zbývajících zpráv do dalšího burstu / backoffu
- Stav: „Běží“ / „Pauza po burstu“ / „Čekám na časové okno“ / „Greylist odklad“
- Poslední SMTP kód a krátké vysvětlení

---

## 2. Handshake a cesta odesílání (vizualizace)

### 2.1 Datový model kroku cesty
```csharp
public enum DeliveryStepKind
{
    DnsMxLookup,
    TcpConnect,
    Ehlo,
    StartTls,
    Auth,
    MailFrom,
    RcptTo,
    Data,
    Quit,
    Error
}

public sealed record DeliveryStep(
    DeliveryStepKind Kind,
    string Title,           // „DNS MX lookup“
    string Detail,          // „mx1.example.com (prio 10)“
    int? SmtpCode,          // 250, 421, null
    string? SmtpText,
    TimeSpan Duration,
    bool Success,
    DateTimeOffset Timestamp);
```

### 2.2 GUI zobrazení cesty
Horizontální nebo vertikální „pipeline“ s barevnými boxy:

```
[DNS MX] → [TCP] → [EHLO] → [STARTTLS] → [AUTH] → [MAIL FROM] → [RCPT TO] → [DATA] → [QUIT]
   ✓          ✓       ✓         ✓          ✓         ✓            ✓          ✓        ✓
```

- Zelená = úspěch
- Oranžová = 4xx (dočasné)
- Červená = 5xx / chyba
- Šedá = ještě neproběhlo

Kliknutím na box se zobrazí detail (čas, kód, text odpovědi, raw řádek z session logu).

### 2.3 Tabulka EHLO capabilities
Po úspěšném EHLO se v GUI zobrazí tabulka:

| Schopnost | Podporováno | Poznámka |
|-----------|-------------|----------|
| STARTTLS  | Ano         | |
| AUTH PLAIN| Ano         | |
| AUTH LOGIN| Ano         | |
| SIZE      | 35651584    | max. velikost zprávy |
| PIPELINING| Ano         | |
| 8BITMIME  | Ano         | |
| SMTPUTF8  | Ne          | |
| CHUNKING  | Ne          | |

---

## 3. Tabulka pozorovaných reakcí a znalostní báze filtrů

### 3.1 Živá tabulka během testu
Sloupce:
- SMTP kód
- Ukázka textu odpovědi
- Počet výskytů
- První výskyt
- Poslední výskyt
- Klasifikace (Greylist / Rate-limit / Permanent / Other)
- Doporučená akce

### 3.2 Vestavěná znalostní báze (FilterKnowledgeBase)
Statická, ale snadno rozšířitelná tabulka běžných limitů:

| Poskytovatel / kontext | Typický limit | Typická reakce | Doporučení |
|------------------------|---------------|----------------|------------|
| Gmail (osobní) | 100–500 msg/den | 421, 454 | Šetrný režim, warm-up |
| Microsoft 365 | dle licence + reputace | 4xx, 550 5.7.1 | Respektovat 4xx, zkontrolovat SPF |
| Shared hosting | 100–300/hod | 421, 550 | Burst+pause, max 2–5 paralel |
| Firemní Exchange | politika | greylist + RBL | Greylist retry 10–15 min |
| Obecné RBL | – | 554, 550 | Před startem zkontrolovat IP |

V GUI záložka „Filtry a limity“ nebo panel v „Tempo a ochrana“:
- DataGrid se znalostní bází (read-only + možnost přidat vlastní řádek)
- Tlačítko „Zkontrolovat source IP na RBL“ (Spamhaus Zen + 1–2 další)

### 3.3 RBL kontrola (volitelná, před startem)
- Dotaz na `sourceIp.zen.spamhaus.org` (DNSBL)
- Výsledek: čistá / listed + odkaz na detail
- Nikdy neblokovat start automaticky – pouze varování

---

## 4. Provider presets

ComboBox „Předvolba poskytovatele“:
- Vlastní (ruční)
- Gmail / Google Workspace
- Microsoft 365 / Outlook
- Seznam.cz
- Obecné sdílené hostování
- Interní / firemní server (agresivní)

Při výběru se nastaví:
- doporučený port + TLS
- interval + jitter
- burst parametry
- zapnutí greylist detekce
- max. concurrency

---

## 5. Datový model rozšíření MailTestOptions

Nové vlastnosti (všechny s defaulty pro zpětnou kompatibilitu):

```csharp
// --- Tempo a ochrana ---
string PaceProfile = "Standard",          // Aggressive | Standard | Gentle | Custom
bool EnableJitter = true,
int JitterPercent = 15,
bool EnableBurstMode = false,
int BurstSize = 10,
int BurstPauseSeconds = 30,
bool EnableProgressiveBackoff = true,
int BackoffAfterSuccesses = 50,
double BackoffMultiplier = 1.5,
int MaxIntervalMs = 60_000,
bool EnablePerRecipientLimit = false,
int MaxMessagesPerRecipient = 20,
int PerRecipientWindowMinutes = 60,
bool EnableSendingTimeWindow = false,
int SendingWindowFromHour = 8,
int SendingWindowToHour = 18,
bool EnableWarmup = false,
string WarmupPhases = "50;150;500",       // zprávy ve fázích
bool DetectGreylist = true,
int GreylistRetryMinutes = 10,
int MaxGreylistRetries = 3,
string ProviderPreset = "Custom",
bool CheckRblBeforeStart = false,
bool CollectObservedResponses = true;
```

---

## 6. Architektura tříd (Core)

| Třída | Zodpovědnost |
|-------|--------------|
| `SmartPaceController` | Výpočet dalšího delaye (jitter, burst, backoff, time window, warm-up) |
| `ObservedResponseCollector` | Sběr SMTP kódů + klasifikace + doporučení |
| `FilterKnowledgeBase` | Statická + uživatelská znalostní báze limitů |
| `DeliveryPathTracker` | Seznam DeliveryStep pro aktuální zprávu / session |
| `GreylistDetector` | Rozpoznání greylist odpovědí |
| `RblChecker` | DNSBL dotaz pro source IP |
| `ProviderPresets` | Továrna doporučených hodnot podle poskytovatele |

SmartPaceController musí být thread-safe a používat se místo (nebo spolu s) stávajícím RateLimiterem.

---

## 7. Pravidla pro implementaci (povinná)

1. Žádná velká změna tempa nesmí porušit existující RateLimiter / CircuitBreaker.
2. Všechny nové volby mají bezpečné defaulty (šetrnější chování).
3. GUI musí vždy zobrazovat **proč** je test pozastaven (burst pauza, časové okno, greylist, backoff).
4. Observed responses se ukládají i do finálního reportu (JSON/HTML).
5. Delivery path se loguje do session logu i do progress updatů.
6. RBL kontrola je volitelná a pouze varuje.
7. Dokumentace v kódu (XML komentáře) musí stačit k vygenerování tooltipů.

---

## 8. Pořadí implementace (doporučené)

1. Rozšíření `MailTestOptions` + validace
2. `SmartPaceController` + integrace do runneru
3. `ObservedResponseCollector` + greylist detekce
4. `DeliveryPathTracker` + rozšíření progress/session logu
5. `FilterKnowledgeBase` + RBL checker
6. GUI záložka „Tempo a ochrana“ + informační panely
7. Provider presets
8. Aktualizace exportu reportů

---

## 9. Příklad textů pro tooltipy (česky)

- **Jitter**: „Náhodná odchylka intervalu snižuje pravděpodobnost, že filtr rozpozná robotický vzor odesílání.“
- **Burst + pause**: „Odešle skupinu zpráv rychleji a poté delší dobu počká – podobá se lidskému chování.“
- **Progressive backoff**: „Po chybách nebo po větším počtu úspěšných zpráv automaticky zpomalí tempo.“
- **Greylist**: „Mnoho serverů při prvním kontaktu odpoví 4xx a očekává opakování za několik minut.“
- **RBL kontrola**: „Ověří, zda source IP není na veřejném blacklistu (Spamhaus). Výsledek je pouze informační.“

Tento dokument je zdroj pravdy pro další vývoj GUI i logiky.
