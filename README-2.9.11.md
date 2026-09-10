# MailLoadTester 2.9.11 — Tempo, ochrana, handshake, cesta

## Co je hotové v Core

### Nové soubory
| Soubor | Účel |
|--------|------|
| `SmartPaceController.cs` | Jitter, burst+pause, progressive backoff, per-recipient limit, časové okno, warm-up, greylist odklad |
| `ObservedResponses.cs` | Sběr SMTP odpovědí + klasifikace + doporučení + FilterKnowledgeBase |
| `DeliveryPath.cs` | Kroky cesty (DNS→TCP→EHLO→…→DATA) + EhloCapabilities tabulka |
| `RblChecker.cs` | DNSBL kontrola source IP (Spamhaus Zen) |
| `ProviderPresets.cs` | Předvolby Gmail, M365, Seznam, Shared, Internal, Gentle, Aggressive… |
| `README-FEATURES-TEMPO.md` | **Závazná specifikace GUI** – všechny ovládací prvky, tooltipy, rozložení záložky |

### Rozšířené
- `MailTestOptions` – všechny nové parametry tempa a ochrany (s bezpečnými defaulty)
- `Validation` – kontroly rozsahů pro nová pole

## Jak z toho udělat klikací GUI

Postupujte **přesně podle** `README-FEATURES-TEMPO.md`:

1. Vytvořte novou záložku **„Tempo a ochrana“**.
2. Přidejte ComboBox „Předvolba poskytovatele“ napojený na `ProviderPresets.All`.
3. Při změně presetu vyplňte všechna pole tempa z `Preset`.
4. Přidejte ovládací prvky podle tabulky v kapitole 1 (interval, jitter, burst, backoff, per-recipient, časové okno, warm-up, greylist).
5. Informační panel: `SmartPaceController.StatusText` + aktuální interval.
6. Tabulka pozorovaných reakcí: `ObservedResponseCollector.Snapshot()` (obnovovat z progressu).
7. Tabulka filtrů: `FilterKnowledgeBase.DefaultEntries` (read-only DataGrid).
8. Pipeline cesty: `DeliveryPathTracker.Steps` – barevné boxy, klik = detail.
9. Tlačítko „Zkontrolovat RBL“ → `RblChecker.CheckSpamhausZenAsync(sourceIp)`.

## Integrace do SmtpTestRunner (minimální kostra)

```csharp
// Na začátku RunSingleAsync:
var pace = new SmartPaceController(options);
var observed = options.CollectObservedResponses ? new ObservedResponseCollector() : null;
var path = new DeliveryPathTracker();

// Před každou zprávou:
await pace.WaitBeforeSendAsync(recipient, ct);
// progress.Report se stavem pace.StatusText

// Po úspěchu:
pace.RecordSuccess(recipient);

// Při SMTP chybě:
if (ex is SmtpCommandException sce) {
    observed?.Record((int)sce.StatusCode, sce.Message);
    if (ObservedResponseCollector.IsGreylist((int)sce.StatusCode, sce.Message))
        pace.RecordGreylist();
    else if ((int)sce.StatusCode is >= 400 and < 500)
        pace.RecordTransientThrottle();
}

// Path kroky přidávat v connection / send logice:
path.Add(DeliveryStepKind.TcpConnect, "TCP connect", host + ":" + port, success: true);
// ...
```

## Důležité zásady (z README-MAINTENANCE + FEATURES)

- Žádná velká alokace před safety preflightem (stále platí z 2.9.11).
- Nové volby mají šetrné defaulty.
- RBL pouze varuje, nestopuje test.
- GUI musí vždy říct **proč** test čeká (burst / greylist / time window / backoff).

## Verze
2.9.11 — zdrojový build s kompletní specifikací a Core třídami pro Tempo a ochranu.
