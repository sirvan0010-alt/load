# MailLoadTester 2.9.38 — audit continuum

Základ: nahraný `MailLoadTester-2.9.32-deep-audit-fixed.zip` (cizí AI).
Toto kolo ověřilo jejich nálezy a doplnilo chybějící opravy.

## Potvrzeno z 2.9.29 (cizí AI) — kód sedí

1. **CircuitBreaker.IsKeyOpen TOCTOU** — conditional `ICollection.Remove(kv)` podle timestampu.
2. **DisposeAsync + `_inFlightConnects`** — čekání bez umělého timeoutu před `_clientCert.Dispose()`
   (use-after-dispose race při TLS handshake).

## Potvrzeno z 2.9.32 (cizí AI) — kód sedí

1. **SmtpConnectionPool** ownership nového `SmtpClient` uvnitř cleanup hranice.
2. **AttachmentPlanner** overflow / peak memory estimate (dle jejich popisu).

## Nové opravy v 2.9.38

| # | Soubor | Problém | Oprava |
|---|--------|---------|--------|
| 1 | SmtpConnectionPool | `IdleConnectionHealthCheckSeconds == 0` = vždy NOOP | **0 = vypnuto** |
| 2 | ObservedResponses | `AddOrUpdate` side-effect `Count++` (možný double-count) | imutabilní update + Snapshot kopie |
| 3 | AdaptiveConcurrencyLimiter | `TrySetCanceled` mimo lock | cancel **pod lockem** |
| 4 | WebhookNotifier | `catch {}` polykal cancel | `catch (OperationCanceledException) throw` |
| 5 | SmartPaceController | `MaxGreylistRetries` nepoužit | limit odkladů greylist pauzy |
| 6 | SmtpTestRunner | Auto-restart metriky jen z posledního běhu | agregace Sent/Failed/… |
| 7 | SmtpConnectivityTester | starý Socks5 konstruktor, bez ProxyList, socket leak | ProxyClientFactory + Dispose socket |
| 8 | Models | path traversal | `ContainsPathTraversal` |
| 9 | MxResolver | cache bez stropu | max 512 + prune |
| 10 | IpV4Rotator | široké CIDR | min `/20` |

## Dokumentace v ZIPu (pro další AI)

- `AUDIT-FIXES-2.9.29.md` … `AUDIT-FIXES-2.9.38.md`
- `VERSION`, `AppVersion.Current`
- Klíčové safety komentáře přímo u lifecycle/cert/CT v Core

## Stále neověřeno běhově

```
dotnet test
dotnet build -c Release
```

(bez .NET SDK v tomto sandboxu)
