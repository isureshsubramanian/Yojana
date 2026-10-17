# Manufacturing production-order contract

This file documents the Serialisation-module half of the contract. Manufacturing remains authoritative for production
orders and stock postings; Serialisation remains authoritative for unit identity, assembly links, custody history and
unit lifecycle. Neither module reads the other's tables.

## Transport and tenant

Manufacturing sends `Manufacturing.ComponentReturned` and
`Manufacturing.ProductionOrderQuantityChangeRequested` as tenant-addressed Wolverine messages. The authenticated
transport envelope supplies the tenant id; it is deliberately not accepted from the JSON payload. The payload's thin
envelope supplies the globally unique message id, legal entity, business time and source actor.

Both messages are idempotent on `(tenant, message_id)`. Serialisation stores the message type, a payload hash and the
result in the same transaction as its effects. Replaying the same payload returns the recorded result with
`duplicate = true` and publishes nothing again. Reusing a message id for a changed payload is refused with
`message_id_payload_mismatch`.

## Component return

`Manufacturing.ComponentReturned` names the production order, the receiving warehouse and exact open assembly-link
ids, grouped by their parent unit ids. On accepted rows Serialisation invokes `RecordDisassembly` with reason
`reverse_issue`. A serialised child then gets a custody move to the named warehouse; lot and quantity-only links have no
unit custody row and are only closed.

Domain failures are returned in `ManufacturingComponentReturnedResult.refusals`, with a named rule and unblocking step.
Valid rows in the same message may still complete. Infrastructure or transaction failures do not produce a result or
receipt and are safe for the bus to retry.

## Quantity reduction and cancellation

`Manufacturing.ProductionOrderQuantityChangeRequested` carries the old and new quantities. An optional explicit
`surplus_unit_ids` list is authoritative and must contain exactly `old_quantity - new_quantity` distinct units. Without
that list Serialisation chooses units commissioned against the production order that have no external document, most
recent allocation first.

Each selected unit is passed through the existing `VoidUnit` command with `initiated_via = integration`. A reduction
uses `work_order_quantity_reduced`; a new quantity of zero uses `work_order_cancelled`. Tenant correction policy supplies
the configured commit roles. Serialisation does not reproduce lifecycle, approval, external-document, component or
serial-reuse rules in this integration handler.

For each request Serialisation publishes `Unit.ProductionOrderReconciled`. It lists the selected units, successfully
voided units and named refusals. Manufacturing may apply the new quantity only when `can_apply_quantity_change` is true
and it has observed the corresponding `Unit.Voided` events. When no allocated surplus units exist, the outcome carries
empty lists and permits the quantity change immediately.

## Ordering and failures

Messages need no broker partition ordering. If a quantity request arrives while a selected unit still has open assembly
links, the outcome reports `components_still_issued` and the unit is not voided. Manufacturing publishes
`Manufacturing.ComponentReturned`, waits for its accepted result, then retries the quantity request with a new message
id. A component cannot be issued to a voided parent: `RecordAssembly` refuses it as
`component_reissue_to_voided_unit`. Reversing the void through a later correction is the unblocking step.

`Unit.Voided` is published only from `VoidUnit` through the unit ledger and transactional outbox. The reconciliation
handler never synthesises that per-unit event.
