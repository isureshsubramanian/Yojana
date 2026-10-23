# CX02 simulator protocol ledger

Source of truth: `docs/plan/research/P10-T02-cx02-protocol-facts.md`, read 2026-10-23, plus the accepted ADR-0004 contract correction. Source labels below are stable fact IDs; this ledger intentionally does not independently re-research them. Protocol-behaviour assumptions are configurable through `Cx02SimulatorOptions`; extension-shape assumptions remain customer-sandbox acceptance items.

| Fact | Simulator value | Confidence | Research section / source |
|---|---|---|---|
| A-02 | Form-encoded client-credentials token route `/{tenant}/oauth2/v2.0/token` | documented | §2 / S3, S18 |
| A-03 | Required scope is the protocol service `.default` scope | documented | §2 / S3 |
| A-09 | Opaque bearer token, `Bearer`, configured `expires_in`, no refresh token | documented | §2 / S18 |
| A-10 | Invalid client/scope gives the identity-provider 400 body fields | documented | §2 / S18 |
| A-11 | Token lifetime is configuration; default 3,599 seconds | inferred | §2 / S17 |
| A-14 | Expired token uses `Authentication_InvalidCredentials`; HTTP 401 by default | ASSUMED | §2 / S12, S3 |
| P-01 | Environment-only and tenant/environment path forms | documented | §3 / S6 |
| P-02 | Tenant GUID accepted by default | ASSUMED | §3 / S6 |
| P-03 | Company collection and `companies({id})` scope | documented | §3 / S3, S7 |
| P-04 | Custom route has publisher/group/v1.0 segments | documented | §3 / S7 |
| P-05 | Custom resources behave as single-GUID API pages | documented | §3 / S7 |
| P-09 | Every entity carries a weak `@odata.etag` | documented | §3 / S7 |
| P-10 | PATCH requires If-Match; wildcard is accepted | documented | §3 / S16, S3 |
| P-11 | Mismatch code is `Request_EntityChanged` | documented | §3 / S12 |
| P-12 | Mismatch status defaults to 412 and can be 409 | ASSUMED | §3 / S12 |
| P-13 | Missing concurrency token uses `BadRequest_InvalidToken` | documented | §3 / S12 |
| P-14 | Successful PATCH is 200 with the updated entity | documented | §3 / S16 |
| P-15 | Collections support `lastModifiedDateTime gt` | documented | §3 / S5 |
| P-16 | `Prefer: odata.maxpagesize`, `$top`, maximum 20,000 | documented | §3 / S15 |
| P-17 | `@odata.nextLink` is on by default and switchable | ASSUMED | §3 / S5, OData standard |
| P-18 | Page size over 20,000 returns 413 | documented | §3 / S2 |
| P-19 | `$batch` accepts at most 100 operations | documented | §3 / S2 |
| P-21 | Errors use the documented code families | documented | §3 / S12 |
| P-22 | OData error envelope is on by default and switchable | ASSUMED | §3 / S12, OData standard |
| P-23 | Standard credit-memo `post` action returns 204 | documented | §3 / S23 |
| P-25 | Custom tracked posting action returns 201 and a created record | inferred | §3 / S23, S8 |
| P-26 | Custom action accepts scalar, object and array JSON parameters by default | ASSUMED | §3 / no source located |
| OUT-01 | The action accepts `documentId`, `logicalReference`, per-kind metadata, line-numbered tracking and all eight contract kinds; `trackedDocumentRequests.logicalReference` is the idempotency lookup | ASSUMED | ADR-0004 correction; no public protocol source located; customer acceptance gate |
| OUT-02 | Standard journals, journal lines and the journal post action use the wire shapes exercised by the simulator | ASSUMED | MOD-16 says journal native; P10-T02 did not record these standard resource fields |
| OUT-03 | A closed posting period returns a 400 `Application_DialogException` whose message says the period is closed | ASSUMED | ADR-0002 edge rule; exact remote error not recorded |
| EXT-01 | Application 26.0, platform 26.0 and runtime 15.0 compile and install in the customer's environment | ASSUMED | Extension manifest; customer acceptance gate |
| EXT-02 | A caller-supplied `SystemId` survives insertion with the preserve-system-id overload | ASSUMED | Extension implementation; no licensed environment used |
| EXT-03 | The non-temporary tracked-request API page is webhook-capable with its single `SystemId` key | ASSUMED | P-06/W-20 inference; customer acceptance gate |
| EXT-04 | Every native table, field and posting codeunit used by the extension has the assumed name and type | ASSUMED | Source implementation; symbols not downloaded under ADR-0003 |
| EXT-05 | Native source document type + number + line values match the contract enum and posted-header resolver | ASSUMED | ADR-0004 correction; customer acceptance gate |
| EXT-06 | The per-kind metadata and tracking member names are accepted by the bound action | ASSUMED | ADR-0004 contract; customer acceptance gate |
| EXT-07 | One tracking array member can be staged per native line before native reservations are created | ASSUMED | Extension implementation; customer acceptance gate |
| EXT-08 | Shipment quantities are negative and receipt quantities positive with unit tracking quantity one | ASSUMED | ADR-0004 consequence; customer acceptance gate |
| EXT-09 | Each posting codeunit call yields exactly one discoverable posted header | ASSUMED | ADR-0004 consequence; customer acceptance gate |
| EXT-10 | A service-order request creates one service item and order, with the service item as the `created` result | ASSUMED | Extension implementation; customer acceptance gate |
| EXT-11 | The bound action can stage the first tracked-request row from its caller-supplied entity key while the collection remains read-only | ASSUMED | ADR-0004 action design; customer acceptance gate |
| R-01 | 6,000 requests in a 300-second sliding window | documented | §4 / S2 |
| R-02 | Window is per principal, including an application principal | documented | §4 / S2 |
| R-03 | Five processing, queued wait default eight minutes then 503 | documented | §4 / S2 |
| R-04 | 100 processed-plus-queued connections | documented | §4 / S2 |
| R-05 | 95 queued, overflow 429 | documented | §4 / S2 |
| R-07 | Separate scripted 408 and 504 operation-timeout faults | documented, conflicting | §4 / S2, S5 |
| R-11 | `Retry-After` is present by default and switchable | ASSUMED | §4 / S2, S5 |
| R-12 | 429 code defaults to `Application_TooManyRequests` and is configurable | ASSUMED | §4 / T2 |
| R-14 | 200 active webhook subscriptions | documented | §4 / S2 |
| W-01 | Standard subscriptions route/body | documented | §5 / S1 |
| W-02 | Custom subscriptions route/body | documented | §5 / S1, S24 |
| W-03 | Validation token query and exact 200 body echo | documented | §5 / S1 |
| W-04 | Handshake defaults to POST, text/plain, ten seconds; all configurable | ASSUMED | §5 / no source located |
| W-05 | Create and renewal both perform the handshake | documented | §5 / S1 |
| W-06 | Subscription lifetime is three days | documented | §5 / S1 |
| W-07 | Renewal uses PATCH and If-Match | documented/inferred | §5 / S1, update-page result |
| W-09 | Optional clientState is echoed | documented | §5 / S1, S24 |
| W-11 | One delivery can contain notifications from multiple subscriptions | documented | §5 / S1 |
| W-12 | created, updated, deleted and collection change types | documented | §5 / S1 |
| W-13 | 30-second coalescing; over 1,000 becomes collection | documented | §5 / S1 |
| W-14 | Collection filter field defaults to `lastDateTimeModified`; URL is delivered verbatim | ASSUMED | §5 / S1 sample |
| W-15 | 408, 429 and 5xx retry for no more than 36 hours | documented | §5 / S1 |
| W-16 | Other response codes delete the subscription | documented | §5 / S1 |
| W-17 | Retry schedule defaults to 1m, 5m, 30m, 2h and is configurable | ASSUMED | §5 / no source located |
| W-18 | HTTP 200 alone means delivered by default; accepted set is configurable | ASSUMED | §5 / no source located |
| W-19 | A line change targets its header resource | documented | §5 / S1 |
| W-23 | Unlicensed-actor change can suppress notification | documented | §5 / S1 |
| W-24 | Actor without scheduling permission can suppress notification | documented | §5 / S1 |
| W-25 | Application-identity changes are suppressed by default and switchable | ASSUMED | §5 / no source located |
| W-27 | DELETE removes a subscription | documented | §5 / S1 |
| B-02 | Business-event retry is off by default and switchable | ASSUMED | §1 / S4 documented absence |
| B-03 | Optional retry performs another attempt | inferred | §1 / T1 |
| B-04 | Dispatch only on commit, never rollback | documented | §1 / S4 |
| B-05 | Catalogue shape and the three used built-in events | documented | §1 / S4 |
| B-06 | Subscription POST returns empty 201 | documented | §1 / S4 |
| B-07 | Notification carries actor, time, company, event, payload, app and clientState | documented | §1 / S4 |
| B-11 | No handshake and no expiry by default; both configurable | ASSUMED | §1 / S4 documented absence |
| B-12 | Application principal may subscribe by default; switchable | ASSUMED | §1 / no source located |
| T-01 | Standard item ledger records have the documented fields and no tracking fields | documented | §6 / S9 |
| T-02 | Standard posted sales shipment header/lines are read-only and have no tracking | documented | §6 / S21, S22 |
| T-03 | Standard posted purchase receipt header/lines are read-only and have no tracking | documented | §6 / S21, S22 |
| T-04 | Custom pages cover absent transfer, return, tracking and service-item resources | documented | §6 / S21 |
| T-06 | Item-entry relations connect posted lines to ledger entries | documented | §6 / S25 |
| T-07 | Reservation and tracking-specification pages model active tracking | documented | §6 / S25 |
| T-08 | Seeded tracked ledger has one entry per tracking number | documented | §6 / S25 |
| T-09 | Serial, lot and package wire names follow inferred underlying fields | inferred | §6 / product knowledge noted there |
| T-10 | Reservation deletions can be emitted at posting | inferred | §6 / S25 |
| T-11 | Custom pages use a single GUID key and support webhooks | inferred | §6 / S1, S7 |
| D-01 | Incoming attachment fields and stream content are accepted | documented | §7 / S10 |
| D-02 | Incoming attachment parent values are validated | documented | §7 / S10 |
| D-03 | Document attachments accept the documented navigation parents | documented | §7 / S11 |
| D-04 | Document-attachment parent strings are configurable | ASSUMED | §7 / S11 contradiction |
| D-05 | Attachment uploads default to the inferred 350 MB request-body limit | inferred | §7 / S2 |
| D-06 | Upload defaults to separate content and can switch to inline | ASSUMED | §7 / no source located |
| S-02 | Service item number and serial are separate fields | documented | §8 / S26 |
| S-06 | Service items require a custom page | documented | §8 / S21 |

## ASSUMED acceptance facts not encoded by the simulator

These remain in the facts manifest and customer checklist even though the simulator does not need a fixed value.

| Fact | Acceptance question | Confidence | Research section / source |
|---|---|---|---|
| A-15 | Which identity-provider conditional-access restrictions apply to the application principal? | ASSUMED | §2 / acceptance checklist |
| L-06 | What licence governs the offline symbol package before any offline compile is introduced? | ASSUMED | §10 / acceptance checklist |
| S-05 | Which service-item number series is configured, and is the required feature enabled? | ASSUMED | §8 / acceptance checklist |
