import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";

const screen = readFileSync(new URL("../src/entitlements/BulkOperation.tsx", import.meta.url), "utf8");
const referenceFields = readFileSync(new URL("../src/corrections/ReferenceFields.tsx", import.meta.url), "utf8");

test("bulk operation asks for records by number, code or name, never by a typed id", () => {
  assert.doesNotMatch(screen, /placeholder="UUID/);
  for (const field of ["DocumentField id=\"bulk-doc\"", "PartyField id=\"bulk-party\"", "ItemField id=\"bulk-item\"", "DocumentField id=\"bulk-source\"", "LegalEntityField id=\"bulk-obligor\"", "LegalEntityField id=\"bulk-backfill-obligor\""])
    assert.ok(screen.includes(`<${field}`), field);
  assert.match(screen, /<select id="bulk-reason"/);
  assert.match(screen, /<select id="bulk-scheme"/);
  assert.doesNotMatch(screen, /\{row\.name\} · \{row\.(familyCode|policyCode)\}/);
});

test("bulk reasons are exactly the amendment reasons the server accepts", () => {
  const vocabulary = readFileSync(new URL("../../src/Serialisation/Yojana.Serialisation.Contracts/EntitlementVocabulary.cs", import.meta.url), "utf8");
  const block = /class AmendmentReason\s*\{([\s\S]*?)public static readonly/.exec(vocabulary)?.[1] ?? "";
  const server = [...block.matchAll(/= "([a-z_]+)";/g)].map((match) => match[1]).sort();
  const screenCodes = [...(/BULK_REASONS = \[([\s\S]*?)\] as const/.exec(screen)?.[1] ?? "").matchAll(/\["([a-z_]+)", "/g)].map((match) => match[1]).sort();
  assert.ok(server.length > 0);
  assert.deepEqual(screenCodes, server);
});

test("document pickers offer only document types recorded by the contract or Serialisation", () => {
  // Fixed from Integration contract examples and Serialisation DocumentRef literals. Additions need matching
  // recording evidence; DocumentField's DocumentPickerType makes every picker use this catalogue.
  const recorded = [
    "adjustment", "delivery", "invoice", "lot", "production_order", "receipt", "return", "sales_order", "stocktake", "transfer",
  ];
  const catalogue = /DOCUMENT_PICKER_TYPES = \[([\s\S]*?)\] as const/.exec(referenceFields)?.[1] ?? "";
  const pickerTypes = [...catalogue.matchAll(/"([a-z_]+)"/g)].map((match) => match[1]);
  assert.deepEqual(pickerTypes, recorded);
  assert.match(referenceFields, /types: readonly DocumentPickerType\[\]/);
  assert.doesNotMatch(screen, /["'](?:goods_receipt|shipment)["']/);
});
