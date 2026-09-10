# 2.9.18

## AdaptiveConcurrencyLimiter — no busy-wait
`AcquireAsync` no longer polls every 20 ms. Waiters register a `TaskCompletionSource`
and are woken on `Release()` or when the adaptive limit increases after `RecordAttempt`.
Cancellation removes the waiter from the queue and does not leak a permit.

## Carried from 2.9.17
- Progress report throttling
- TemplateTags StringBuilder fast-path
- OAuth2 / dry-run GUI tooltips
