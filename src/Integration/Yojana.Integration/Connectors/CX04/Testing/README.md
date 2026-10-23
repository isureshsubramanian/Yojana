# CX04 simulator harness

`Cx04Simulator` is a deterministic, in-process HTTP server for adapter and connector-edge tests. The
production Integration assembly excludes this directory; test projects link the sources explicitly.
It has no database and calls no external system.

## Public harness surface

- Construct with `Cx04SimulatorOptions` and, when push delivery must stay in-process, an optional
  `HttpMessageHandler`.
- Use `BaseUri` as the connection endpoint. Seed contract records with `SeedEntity`, configure the
  per-entity/version poll field with `SetModifiedDateField`, and configure item serial assignment with
  `SetSerialAssignment`.
- Configure one narrow query per document type with `ConfigurePushQuery`, then publish its change rows with
  `PublishDocumentChangesAsync`. Aggregate queries are rejected. `PublishNotificationsAsync` remains the
  lower-level fault-test seam; inspect or manually resend the parked list with `ParkedNotifications` and
  `ResendParkedNotificationAsync`.
- Script failures only through `Faults`: dropped create reply, duplicate or reversed notification batch,
  429 storm, token expiry during a run, leaked sessions at the limit, disabled action, forbidden request,
  truncated notification, pre-send loss, and held parallel requests. `ReleaseHeldRequests` releases the
  last case.
- Read-only observations (`LicenceProfile`, request counters, `Files`, notifications and
  `LastHandlerFailure`) support assertions without exposing the stores that implement the protocol.

The linked tests should use dates in the past. Fixture names and data are fictional IT-hardware data.

## Assumed and conflicting behaviours

These are simulator choices, not claims about a live customer system. Defaults follow the research
recommendation. The alternative is available so the customer acceptance run can replace the assumption
without rewriting a test scenario.

| Option | Default | Alternative |
|---|---|---|
| `QueueLimitReading` | per-tier 5/10/20/40 | fixed 20 |
| `RefreshExpiryMode` | 30-day absolute chain | 90-day sliding chain or disabled refresh |
| `AllocationQuantityBehaviour` | quantity on line and allocations sum to it | allocations only; sending both reproduces the reported doubling |
| `EmptyThrottleResponse` / `IncludeRetryAfterOnThrottle` | `{}` and no `Retry-After` | shaped error body and header |
| `IdWinsWhenBothProvided` | `id` wins | key fields win |
| `RequireSerialQuantityOne` | serial allocation quantity must be one | accept another quantity |
| `SupportRmAllocations` | RM uses the RC allocation shape | reject RM allocations |
| `AttachmentSizeLimitBytes` | 25 MiB | caller-configured limit |
| `RunningActionStatusCode` | 202 while running | caller-configured status |
| `ReceiptReleaseActionName` | `ReleasePurchaseReceipt` | caller-configured action name |
| `ReapIdleSessionsBelowLimit` | never reap below the limit | reap after ten idle minutes |
| `StablePagingOrder` | entity-key order | seeded order |
| Modified field | Sales order: `LastModified`; other pinned entities: `LastModifiedDateTime` | `SetModifiedDateField` per entity/version |
| `PushRetryInterval` | 30 seconds | caller-configured interval |
| Push authentication | configurable static shared-secret header | caller-configured header/value |
| Manufacturing fields | no invented fixed field catalogue; the test seeds the fields its adapter reads | acceptance run replaces the seed shape |

The assumed validation failures deliberately say `Simulator assumption` in `exceptionMessage` or the
field-level `error`. This keeps a guessed remote message from being mistaken for a documented one.

## Consumer notes for the generic edge harness

The harness should treat HTTP as the behavioural contract. It may use only the public seed, option,
fault and observation members above. It must not reach into the governor, token/session dictionaries,
entity store or parked-notification list. `PROTOCOL-SOURCES.md` is the trace from each encoded fact to the
research record and public source.
