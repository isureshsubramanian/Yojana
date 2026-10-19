# Connection worker isolation

Every `(tenant, connection)` pair has its own semaphore lane. Work for one connection therefore cannot occupy another
connection's permits. Callers acquire the lane immediately before connector work and release it after the attempt,
including failures.

P8-T11 adds a token bucket per same `ConnectionWorkerKey`. Its worker pipeline is:

1. acquire the connection lane;
2. await that connection's token bucket and any `Retry-After` delay;
3. perform one connector operation;
4. record health/audit results and release the lane.

The token-bucket registry must key on the full tenant and connection pair. It may decorate `IConnectionWorkerIsolation`,
but must not reach into the semaphore implementation or share tokens between keys.
