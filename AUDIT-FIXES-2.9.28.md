# MailLoadTester 2.9.28 — další hluboký audit

## Opravené reálné nálezy

### 1. Phantom per-recipient reservation při STOP/cancellation

`SmtpTestRunner` rezervoval slot pro příjemce v `SmartPaceController.WaitBeforeSendAsync()`, ale následný `RateLimiter.WaitAsync()` nebo `AdaptiveConcurrencyLimiter.AcquireAsync()` probíhal ještě před hlavním `try/finally`, který rezervaci uvolňuje.

Pokud tedy STOP/cancellation nastal právě v této mezeře, rezervace zůstala v `RecipientWindow.Reserved`. Při zapnutém limitu na příjemce mohl takový phantom slot dočasně blokovat další zprávy pro daného příjemce.

Oprava: RateLimiter + AdaptiveConcurrency jsou nyní v ochranném `try/catch`; při jejich zrušení se rezervace příjemce okamžitě vrátí.

### 2. Race při shutdownu a klientském certifikátu

`SmtpConnectionPool` už měl `_inFlightConnects`, ale samotné zvýšení čítače nebylo atomické s kontrolou `_disposed`. Teoretická sekvence:

1. `RentAsync` projde kontrolou `_disposed`.
2. `DisposeAsync` nastaví shutdown a uvidí `_inFlightConnects == 0`.
3. `DisposeAsync` uvolní `_clientCert`.
4. starší `RentAsync` teprve poté zvýší `_inFlightConnects` a začne TLS handshake.

To mohlo použít již uvolněný certifikát. Runner běžně čeká na workery před `DisposeAsync`, ale pool je nyní bezpečnější i proti konkurenčnímu shutdownu.

Oprava: `_lifecycleLock` atomicky páruje kontrolu `_disposed` s vstupem do in-flight connection window; `DisposeAsync` nastavuje shutdown pod stejným zámkem.

## Ověření

- Statický audit zdrojů 2.9.27 proveden.
- Lokální prostředí tohoto auditu nemá nainstalované `dotnet`, takže zde nebylo možné spustit `dotnet test` ani Windows Release build.
- Doporučené ověření na Windows/.NET 8:
  - `dotnet test -c Release`
  - `dotnet build -c Release`
  - případně `dotnet publish -c Release --self-contained true`
