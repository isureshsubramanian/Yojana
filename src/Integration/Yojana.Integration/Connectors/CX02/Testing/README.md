# CX02 simulator test surface

`Cx02Simulator` is an in-process HTTP implementation of the P10-T02 protocol record. A later connector-generic harness can depend only on this public surface:

- `BaseUri`, `TokenUri`, `StandardApiUri` and `CustomApiUri` locate protocol endpoints.
- `Seed` supplies deterministic collection records without reaching into simulator state.
- `QueueWebhookChange`, `DispatchWebhooksAsync` and `PublishBusinessEventAsync` drive hint delivery.
- the named one-shot fault members drive reply loss, notification ordering/duplication, lapsed subscriptions, throttling, token expiry, actor suppression and both documented timeout variants.
- `RequestGate` permits deterministic concurrent-request tests.

The simulator is compiled into test projects only. It has no dependency on an adapter, database, Wolverine handler or wall clock. Adapter tests must use HTTP and the surface above rather than its private protocol state.

TODO(P10-T14): register this simulator with the connector-generic edge harness after that harness is available on this branch.
