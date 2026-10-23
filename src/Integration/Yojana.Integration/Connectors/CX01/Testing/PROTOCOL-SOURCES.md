# CX01 protocol sources

This file separates verified protocol facts from assumptions encoded by the adapter and simulator. The source
application's installed-base status field is `StatusOfSerialNumber`; `sns_Active` and `sns_Terminated` are retained
from the verified P8/P10 implementation.

## ASSUMED native-warranty status values

The following defaults are unverified assumptions. They are per-connection options and are never treated as fixed
protocol facts:

- module `in_repair` → `sns_InRepair`
- module `returned` → `sns_Returned`
- module `loaned` → `sns_Loaned`

Stable fact ids: `NW-01` (In Repair), `NW-02` (Returned), `NW-03` (Loaned).

## Other ASSUMED acceptance facts from the verified appendix

- `OPEN-01`: full-line return serial behaviour.
- `OPEN-02`: different-location return serial behaviour.
- `OPEN-03`: approval behaviour for a non-superuser originator.
- `OPEN-04`: attachment size limit.
- `OPEN-05`: change notifications on a hosted tenant.

A connection may omit any one of these values. The adapter then sends `sns_Active` and records a health note instead
of failing the outbound command.

## P10-T15 customer-sandbox acceptance checklist additions

- [ ] Verify the accepted remote code for the In Repair card status; record the value in connection settings.
- [ ] Verify the accepted remote code for the Returned card status; record the value in connection settings.
- [ ] Verify the accepted remote code for the Loaned card status; record the value in connection settings.
- [ ] Confirm each status is visible on a card after an API update and survives a re-read.
- [ ] Confirm returning each state to Active is accepted and visible after a re-read.
