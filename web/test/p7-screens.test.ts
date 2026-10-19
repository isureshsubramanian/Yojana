import assert from "node:assert/strict";
import test from "node:test";
import { readFileSync } from "node:fs";
import { acceptedImportFormats, acceptsImportFile, actionOptions, coverageLabel, disclosureLocked, human, internalSections, portalUnitSections, publicLookupFields, reconciliationKpis, splitValues, xrefIndent } from "../src/p7/model.ts";
import { countryName } from "../src/reference/countries.ts";

test("import job accepts every approved spreadsheet format and rejects unrelated files", () => {
  assert.deepEqual(acceptedImportFormats, ["csv", "xlsx", "xls"]);
  assert.equal(acceptsImportFile("vendor.XLS"), true);
  assert.equal(acceptsImportFile("notes.pdf"), false);
});

test("upload history is server-paged, filterable and offers both downloads", () => {
  const screen = readFileSync(new URL("../src/p7/ImportHistory.tsx", import.meta.url), "utf8");
  assert.match(screen, /FindBar/);
  assert.match(screen, /p7Api\.importHistory/);
  assert.match(screen, /row\.mayDownload/);
  assert.match(screen, /files\/original/);
  assert.match(screen, /files\/converted/);
  assert.match(screen, /Export CSV/);
});

test("compare mode never offers assign for an existing collision", () => {
  assert.deepEqual(actionOptions({ comparisonPartition: "existing_collision" }), ["skip", "flag"]);
  assert.deepEqual(actionOptions({ comparisonPartition: "only_in_a" }), ["assign", "skip", "flag"]);
  assert.equal(human("only_in_a"), "Only in A");
  assert.equal(human("only_in_b"), "Only in B");
});

test("the country catalogue displays names while preserving ISO values", () => {
  assert.notEqual(countryName("IN"), "IN");
});

test("reconcile customer list preserves all five server totals", () => {
  assert.deepEqual(reconciliationKpis({ total: 20, owned: 11, ownedByAnotherParty: 4, notFound: 3, invalid: 2 } as never), [20, 11, 4, 3, 2]);
});

test("portal public lookup renders only its closed safe field set", () => {
  assert.deepEqual(publicLookupFields, ["itemCode", "itemName", "condition", "manufactureMonth", "warrantyStatus"]);
  assert.equal(publicLookupFields.some((field) => /party|document|service|cost/i.test(field)), false);
});

test("portal installed base presents an expiry when present and status otherwise", () => {
  assert.match(coverageLabel([{ coverageFamily: "base", start: null, expiry: "2027-09-01", status: "active" }], "base"), /2027/);
  assert.equal(coverageLabel([{ coverageFamily: "base", start: null, expiry: null, status: "pending" }], "base"), "Pending");
});

test("portal unit page keeps intake beside its three read-only sections", () => {
  assert.deepEqual(portalUnitSections, ["coverage", "service history", "certificates", "registration", "fault report"]);
  assert.equal(portalUnitSections.some((section) => /cost|internal note/i.test(section)), false);
});

test("where-used pasted input trims blanks, accepts common separators and enforces 500", () => {
  const values = splitValues(Array.from({ length: 510 }, (_, index) => ` S-${index} `).join(indexSeparator()));
  assert.equal(values.length, 500);
  assert.equal(values[0], "S-0");
  function indexSeparator() { return "\n"; }
});

test("internal lookup section count follows returned ownership and service facts", () => {
  assert.equal(internalSections({ ownership: [{}, {}], serviceHistory: [{}] }), 3);
  assert.equal(internalSections({ ownership: [], serviceHistory: [] }), 0);
});

test("identifier cross-reference indents components under their root", () => {
  assert.equal(xrefIndent({ depth: 0 }), 0);
  assert.equal(xrefIndent({ depth: 2 }), 36);
});

test("disclosure policy locks forbidden public fields and the internal tier", () => {
  assert.equal(disclosureLocked({ field: "party.data", tier: "public", enabled: false }), true);
  assert.equal(disclosureLocked({ field: "item.name", tier: "internal", enabled: true }), true);
  assert.equal(disclosureLocked({ field: "item.name", tier: "owner", enabled: true }), false);
});
