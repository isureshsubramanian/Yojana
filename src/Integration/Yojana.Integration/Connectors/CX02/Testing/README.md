# CX02 simulator test surface

`Cx02Simulator` is an in-process HTTP implementation of the P10-T02 protocol record. The connector-generic edge harness depends only on this public surface:

- `BaseUri`, `TokenUri`, `StandardApiUri` and `CustomApiUri` locate protocol endpoints.
- `Seed` supplies deterministic collection records without reaching into simulator state.
- `QueueWebhookChange`, `DispatchWebhooksAsync` and `PublishBusinessEventAsync` drive hint delivery.
- the named one-shot fault members drive reply loss, closed-period rejection, notification ordering/duplication, lapsed subscriptions, throttling, token expiry, actor suppression and both documented timeout variants.
- the standard credit-memo, journal and attachment resources plus the tracked posting action exercise outbound query-before-create and upload behaviour.
- `RequestGate` permits deterministic concurrent-request tests.

The simulator is compiled into test projects only. It has no dependency on an adapter, database, Wolverine handler or wall clock. Adapter tests use HTTP and the surface above rather than its private protocol state. The generic harness declares discovery-time reasons for scenarios whose grouped-delivery fixture cannot be represented by CX02's one-row-per-tracking-number feed.
