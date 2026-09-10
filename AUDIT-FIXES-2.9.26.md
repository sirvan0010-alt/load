# MailLoadTester 2.9.26 — cert handle leaks

## Nálezy
`X509Certificate2` (IDisposable, nativní crypto handle) se nikde nedisposoval:

1. `SmtpConnectionPool._clientCert` — vytváří se v konstruktoru poolu, ale
   `DisposeAsync()` ho nikdy neuvolnil. Každé nové spuštění testu s nastaveným
   klientským certifikátem = další nedisposovaný handle po celou dobu běhu
   procesu (GUI session).
2. `SmtpConnectivityTester.TestAsync` — `cert` lokální proměnná, nikdy
   nedisposovaná (SmtpClient.Dispose() nepřebírá vlastnictví certifikátů
   přidaných do ClientCertificates). Každé kliknutí na "Test Connection"
   s nastaveným certifikátem = další leak.

## Oprava
- `SmtpConnectionPool.DisposeAsync()`: `_clientCert?.Dispose();`
- `SmtpConnectivityTester.TestAsync`: `using var cert = ...;`
- `ClientCertificateHelper.Load`: explicitní `X509KeyStorageFlags.EphemeralKeySet`,
  aby se privátní klíč nikdy nepersistoval na disk/registry.

## Auditováno bez nálezu
- `RateLimiter.cs` — cancellation-safe, stejný vzor jako SmartPaceController.
- `ProxyClientFactory.cs` — čistý (drobná, nekritická limitace: IPv6 proxy host
  bez hranatých závorek se `host:port` parserem neuloví, ale nepadá).
