# CX02 extension HTTP contract v1.0

This directory is the source-controlled boundary between the tenant-installed extension and the CX02 adapter. It defines JSON only; it does not contain extension source. Every page uses a single GUID `id`, exposes `@odata.etag`, and carries `lastModifiedDateTime` as the polling change marker. Collection responses wrap these page records in the standard `value` array and may carry `@odata.nextLink`.

The adapter needs the pages below to recover per-unit history that the standard API does not expose. Field names are our camel-case wire names. “Documented” means the underlying table purpose or field is named in P10-T02 research §6. “Assumed” means the research did not establish the underlying field name or type; these mappings must be checked at the first customer acceptance run.

| Page / action | What it carries | Keys and change marker | Adapter reason | Assumed fields |
|---|---|---|---|---|
| `trackedItemLedgerEntries` | Ledger identity, item, posting/source/document facts, quantity and serial/lot/package | `id`; `lastModifiedDateTime` | Authoritative posted unit movement; the standard ledger omits tracking | `serialNumber`, `lotNumber`, `packageNumber` names are inferred from T-09; `locationCode` is assumed |
| `itemEntryRelations` | A posted document line to ledger-entry relation, keyed by document type + number (maximum 20 characters) + line | `id`; `lastModifiedDateTime` | Joins a posted line to its per-tracking-number ledger entries | The native source fields and posted-header resolution are assumed; `sourceDocumentId` is nullable when no posted header can be resolved |
| `reservationEntries` | Active document tracking assignment, keyed by document type + number (maximum 20 characters) + line | `id`; `lastModifiedDateTime`; read-only | Observes native serial/lot/package allocation; writes happen only inside the posting action after the document line exists | All wire field names and types are assumed from the T-07/T-10 table purpose |
| `trackingSpecifications` | Historic/active tracking quantity, keyed by document type + number (maximum 20 characters) + line | `id`; `lastModifiedDateTime` | Reconciles the allocation that posting consumed | The native source fields and posted-header resolution are assumed; `sourceDocumentId` is nullable when no posted header can be resolved |
| `serviceItems` | Service-item number and sold serial | `id`; `lastModifiedDateTime` | Keeps the distinct service number as a second unit identifier | `number` and `serialNumber` concepts are documented by S-02; `itemNumber`, `customerNumber`, `status` and their wire mappings are assumed |
| `postedTransferShipments` | Posted transfer header | `id`; `lastModifiedDateTime` | Finds outbound transfer documents absent from the standard API | All business fields are assumed; T-04 documents only absence from the standard API |
| `postedTransferShipmentLines` | Posted transfer line and quantity | `id`; `lastModifiedDateTime` | Resolves transferred items and document-line relations | All business fields are assumed |
| `postedTransferReceipts` | Posted transfer receipt header | `id`; `lastModifiedDateTime` | Finds inbound transfer documents absent from the standard API | All business fields are assumed |
| `postedTransferReceiptLines` | Posted transfer receipt line and quantity | `id`; `lastModifiedDateTime` | Resolves received items and document-line relations | All business fields are assumed |
| `postedReturnReceipts` | Posted return receipt header | `id`; `lastModifiedDateTime` | Finds return receipts absent from the standard API | All business fields are assumed |
| `postedReturnReceiptLines` | Posted return receipt line and quantity | `id`; `lastModifiedDateTime` | Resolves returned items and document-line relations | All business fields are assumed |
| `trackedDocumentRequests` | The staged logical reference, status, posted/created result and coded failure | `id`; `lastModifiedDateTime`; read-only | Query-before-create idempotency lookup and the entity returned by the action | Client-supplied ids, webhook eligibility, status persistence and the 201 entity result are acceptance facts |
| `postTrackedDocument` request | Document kind/id, logical reference, per-kind metadata and line-level serial, lot, package, quantity and `sourceLineNumber` assignments | Bound tracked-request `id`; no change marker | Stages the request, creates the native lines, then writes reservations and invokes posting | Bound-action scalar/array support is ASSUMED P-26; every accepted kind and metadata key is fixed by the request schema |
| `postTrackedDocument` response | The `trackedDocumentRequests` entity for the request | `id`; `completedAt`; HTTP 201 | Returns `posted` for posting kinds and `created` for `serviceOrder`; the same entity is the retry lookup | The response-through-action-context pattern and exactly-one-result lookup remain acceptance facts |

The action kinds are `salesShipment`, `salesCreditMemo`, `purchaseReceipt`, `transferShipment`,
`transferReceipt`, `returnReceipt`, `itemJournal` and `serviceOrder`. The request schema uses `oneOf` to
state each kind's required metadata. Tracking is never created through `POST /reservationEntries`.

The extension may add nullable fields in a future minor contract, but it must not remove fields, change meanings, or make an optional field required within v1.0. The adapter follows collection next links verbatim and treats webhook and business-event messages as hints; `lastModifiedDateTime` polling remains the recovery baseline.
