# MailLoadTester 2.9.39 — extracted ZIP manifest

Original upload: `MailLoadTester-2.9.39-FINAL.zip`.

The archive was successfully extracted and inspected locally. It contains 110 ZIP entries, including the complete Core, GUI and test source trees. The source entries are UTF-8 text; no binary payloads were found.

## Top-level extracted files

- `AUDIT-FIXES-2.9.10.md` through `AUDIT-FIXES-2.9.39.md`
- `AUDIT-FIXES-DEEP-PHASE1-3.md`
- `AUDIT-NEXT-ROUND-RECOMMENDATIONS.md`
- `AUDIT-ROUND4-PROGRESS.md`
- `AUDIT-STATUS-2.9.14.md` through `AUDIT-STATUS-2.9.16.md`
- `DEEP-AUDIT-2.9.11.md`
- `MailLoadTester.sln`
- `README-2.9.11.md`
- `README-AUDIT-CHAIN.md`
- `README-BUILD.md`
- `README-FEATURES-TEMPO.md`
- `README-MAINTENANCE.md`
- `VERSION`
- `build.bat`
- `build-installer.bat`
- `installer/MailLoadTester.iss`

## Source trees

`src/MailLoadTester.Core/` contains 39 C# source/project files, including:

- `SmtpConnectionPool.cs`
- `AdaptiveConcurrencyLimiter.cs`
- `RateLimiter.cs`
- `SmartPaceController.cs`
- `CircuitBreaker.cs`
- `SmtpTestRunner.cs`
- `TestStateMachine.cs`
- `ObservedResponses.cs`
- `DeliveryPath.cs`
- `ProviderPresets.cs`
- `IpV4Rotator.cs`
- `IpV6Rotator.cs`
- `ProxyRotator.cs`
- `BandwidthLimiter.cs`
- `WebhookNotifier.cs`

`src/MailLoadTester.Gui/` contains the WinForms GUI project and source files.

`tests/MailLoadTester.Tests/` contains 22 C# test/project files covering concurrency, pacing, SMTP pool, state machine, rate limiting, IP/proxy rotation, and validation.

The complete original ZIP remains the authoritative uploaded artifact. This GitHub reference branch stores the architectural comparison and extracted-reference documentation without turning MailLoadTester into a production dependency of MailSenderEngine.
