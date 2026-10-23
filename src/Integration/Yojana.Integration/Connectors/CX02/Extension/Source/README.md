# CX02 tenant-side extension source

This directory contains the source-only per-tenant extension for the frozen HTTP contract in `../Contract/v1.0`. It is deliberately excluded from the .NET module's compile, content and embedded-resource items. The C# tests copy it only as test input and compare its declared API shape with the JSON Schemas.

## Customer-sandbox acceptance gate

The acceptance operator supplies the customer's licensed sandbox and approved development toolchain. In that sandbox, the operator:

1. selects symbols compatible with the customer's installed application and platform versions;
2. compiles this directory and runs the standard code, per-tenant-extension and user-interface analysers;
3. installs and upgrades the generated package in the sandbox;
4. assigns the application registration as described in `APPLICATION-PERMISSIONS.md`;
5. runs the CX02 live edge suite and every CX02 acceptance-checklist item, including API discovery, webhook eligibility, posting, idempotent retry and the action response;
6. records the compiler, analyser, install and live-suite evidence before the tenant can go live.

No compiler, analyser, symbol package or container image is downloaded or run by this repository task. Their licence and the customer's installed version are acceptance inputs.

## Versioning

`APIVersion = 'v1.0'` is fixed on every API page. Once a customer installs v1.0, that wire contract is frozen. Additive optional fields require a reviewed compatible release. Removing a field, changing its type or meaning, changing an entity name, or making an optional field required needs a new `APIVersion` and parallel pages; it must not mutate v1.0.

## Assumptions to verify at acceptance

- The customer accepts application/platform 26.0 and runtime 15.0, or the manifest is lowered without changing the v1.0 wire contract.
- A custom API page accepts a caller-supplied `SystemId` on insert when the record is inserted with the system identifier preserved.
- A service-enabled bound action accepts the contract's scalar, `JsonObject` and `JsonArray` parameters, and a created action result can resolve through the dedicated response page with the stated JSON body and status.
- The non-temporary `Yojana Tracking Request` table is listed as webhook-capable with its single `SystemId` API key. It is the compatibility seam between contract GUIDs/string document types and native reservation keys/document numbers.
- The public table and field names used by the pages and posting codeunit are present with the types assumed by the v1.0 schemas on the customer's installed version.
- `Item Entry Relation.Source ID` and `Tracking Specification.Source ID` may contain document numbers rather than GUIDs. Until acceptance establishes a universal document resolver, those two pages fall back to their row `SystemId`; consumers must not treat that fallback as a proven document join.
- The action body metadata keys are `customerNumber`, `shipToCode`, `vendorNumber`, `fromLocationCode`, `toLocationCode`, `itemNumber`, `serialNumber` and `description` for the document kinds that need them. The v1.0 schema deliberately leaves `metadata` open and does not document these required-by-kind keys.
- The request's tracking array has one member for each staged reservation row, in the same posting request. The action validates the count but native reservation creation uses the staged rows, which are the durable idempotency input.
- Negative sales or transfer shipment quantities and positive receipt quantities map to native surplus tracking entries with quantity-per-unit equal to one.
- The named posting codeunits and their posted-header lookup fields produce one discoverable posted record for each successful call.
- A service-order request creates a service item and an order; the response uses the service item as the durable result. The contract's fixed `posted` status does not distinguish this non-posted document state.
- The response page's collection route is an implementation detail used to materialise the 201 body. Callers use only the bound action route from the contract.

The acceptance gate must fail closed on any assumption that does not hold. Correct the source under a new extension build; if the correction changes the v1.0 wire contract, introduce a new API version instead.
