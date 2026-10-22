# Coexistence rollback runbook ("stop the sync")

This runbook applies while the service module runs beside the customer's existing ERP (coexistence mode). It is
shipped with the product so the customer's administrator and the implementation team have the same text.

## What coexistence changes in the ERP

In the default outbound mode (`reference_only`) the module writes only reference data it owns into the ERP:

- the RMA number into a user-defined field on the return document;
- the certificate link on the equipment card.

It never posts stock movements, financial documents or serial numbers. The ERP keeps serial existence, stock
location, document links and finance. Because nothing else is changed, rolling back never needs a data repair in the
ERP. If the tenant opted in to `documents` mode, documents already posted stay posted and are reversed in the ERP by
its own users, as for any other document.

## Stopping the sync

1. In **Integration → Connections**, open the connection and choose **Stop the sync** (or call
   `POST /api/integration/connections/{id}/stop-sync` with a reason). A reason is mandatory and is kept in the
   connection's audit trail.
2. The connection becomes **stopped**. From that moment:
   - nothing is read from the ERP (polling, notifications, backfill, daily check and drift scan all skip it);
   - nothing is written to the ERP (waiting outbound commands stay queued; none is sent);
   - the ERP's notification subscription for this connection is paused where the ERP supports it, so the ERP does
     not mark the stream as failed. If pausing was not possible the result says so; deliveries are then refused and
     the ERP retries them later.
3. The module keeps working for everything it owns. Users continue to use the ERP as before for stock and finance.

Stopping works even when the ERP is unreachable: the local stop never waits for the ERP.

## Disabling module writes for longer

If the stop will last, also set the connection's outbound mode to `none`: waiting commands then never leave, even
after the sync resumes. While the sync is stopped they stay queued and are counted on the connection's health page.
When sending is enabled again, each waiting command is checked against the connection's maximum outbox age as it is
picked up; one that is too old moves to **Messages needing attention** instead of being sent, where it can be
discarded with a reason (a discard is recorded, never silent).

## Re-pointing users

For a full rollback of an object (for example installed-base ownership), tell users to maintain it in the ERP again
and switch the ERP's permissions back if they were made read-only during the parallel run. The module's history is
kept; nothing is deleted.

## Resuming

Choose **Resume the sync** (`POST /api/integration/connections/{id}/resume-sync`). Polling catches up from its
saved position with its overlap window, so changes made in the ERP while the sync was stopped are read; the
notification subscription is released, and the watchdog replays notifications that failed while it was stopped.
Re-running the import is idempotent: documents already received are recognised and not applied twice.

## Conflicts

While both systems run, the ERP stays authoritative for stock and documents and the module for installed-base owner,
condition, entitlement, RMA and certificates. When the drift scan finds a module-owned field edited in the ERP without
a document explaining it, the change is parked in **Coexistence conflicts**. Nobody's data is overwritten
automatically. A person chooses one of:

- **Keep the module value** — an integration task asks someone to set the value back in the ERP; the next scan
  closes the conflict when it sees the value restored.
- **Accept the ERP value** — the ERP value becomes the new baseline; if the module must change too, a person changes
  it in the module.
- **Dismiss** — not a real conflict; the ERP value becomes the baseline.

## Parallel run

The parallel-run checklist (30, 60 or 90 days) lists the measured items (daily check clean, no message waiting longer
than seven days, conflict queue empty, recent drift scan) and the confirmed items (active phase, ERP read-only for the
owned objects, one month-end close, rollback rehearsed, users moved). Rehearse this runbook once during the parallel
run and confirm the "Rollback rehearsed" item with the date and who did it.

## Final load and the delta load at cutover

From the final migration load on, movements (deliveries, receipts, returns, transfers, adjustments) reach the module
only through the connector:

1. **Stop the sync** of the source connection before the final load; the product refuses a final load while it runs.
2. The final load records the source server's clock when its extraction began and, when it completes, sets every
   polling watermark of the connection to that time. The connection stays stopped.
3. **Resume the sync.** The first poll reads the documents posted since the extraction. Keep postings frozen while the
   extraction runs: a document posted during the extraction can appear both in the migrated history and in that poll.
4. At cutover, extract the source again and map it, then a tenant administrator starts the **delta load** by hand. It
   adds serials that are new in the source, replaces the migrated cover the source changed (refused, and listed, for a
   unit the module has already worked on), lists owner changes that have no delivery behind them as conflicts
   (accept or dismiss; nothing is written), and lists serials gone from the source (nothing is removed).
5. A delta load can be undone on its own, latest first; it is refused while any unit it changed has later facts.
