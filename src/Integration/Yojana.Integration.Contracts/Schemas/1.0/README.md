# sw.integration/1.0

Schemas and examples are embedded resources of Yojana.Integration.Contracts. Major versions remove, rename, retype, make fields mandatory, or change enum meaning; minor versions only add optional fields or enum values. The gateway accepts the current and previous major. Readers ignore unknown fields. Unknown enum strings are mapped to value `other` while retaining the source string as `raw_value`. Dates include an ISO 8601 offset; quantities carry a UOM and money carries a currency.

The compatibility harness uses a deliberately small structural validator over System.Text.Json. It implements the subset used here (object/array/string/number/boolean/null, required, properties, const and local/external $ref). No JSON-schema package is introduced because these tests need structural contract compatibility, not a second runtime validation engine; adapter boundaries still deserialize to the typed records.

`ComponentsIssued` is the additive ADR-0025 message. It carries one source document and one production order, with
`lines` in the common `CanonicalLine` shape. A mixed source issue can therefore yield an `InventoryAdjusted` message
for ordinary lines and one `ComponentsIssued` message for each production order represented by its based lines. This
addition advances the contract bundle's current minor version to 1.1; the public major-version schema directory stays
at `Schemas/1.0` because no existing field or meaning changed.

