# Connection worker isolation

Every `(tenant, connection)` pair has its own semaphore lane. Work for one connection therefore cannot occupy another
connection's permits. Callers acquire the lane immediately before connector work and release it after the attempt,
including failures.

P8-T11 adds second and minute token buckets plus a durable UTC-day counter per the same
`ConnectionWorkerKey`. Its worker pipeline is:

1. acquire the connection lane;
2. await that connection's token buckets and any connection-wide `Retry-After` delay;
3. perform one connector operation;
4. record health/audit results and release the lane.

The token-bucket registry must key on the full tenant and connection pair. It may decorate `IConnectionWorkerIsolation`,
but must not reach into the semaphore implementation or share tokens between keys.

Connector throttle declarations seed `connection_throttle_profile` during onboarding. The saved profile is editable
per connection and is read before every HTTP attempt; declarations are data defaults rather than product rules. A
changed concurrency limit takes effect when the connection's current semaphore generation becomes idle, without
opening a second lane.

Adapters mark a non-safe operation with `ConnectionRateLimitedHandler.IdempotentRequest` only when their caller can
prove it is idempotent. The handler never infers query-before-create: that belongs to the outbox relay. A 429 or 503
pauses this worker, so every call for the connection observes the capped `Retry-After`, while other worker keys remain
independent.

Daily usage is persisted through `IConnectionDailyBudgetStore`. Crossing the saved threshold and reaching the cap
publish separate `ConnectionCallBudgetAlert` records, with their once-per-UTC-day flags saved in the same optimistic
write as usage. P8-T13 consumes that narrow record; the throttle worker does not know the health projection.
