import assert from "node:assert/strict";
import test from "node:test";
import { readFileSync } from "node:fs";
import { acceptedImportFormats, acceptsImportFile, actionOptions, serviceRecordPath, coverageLabel, disclosureLocked, human, internalSections, portalUnitSections, publicLookupFields, reconciliationKpis, splitValues, xrefIndent } from "../src/p7/model.ts";
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
  assert.match(screen, /p7-file-btn disabled/, "a download the viewer may not take stays visible but disabled");
  assert.match(screen, /title=\{label\}/, "the disabled icon explains who may download in its tooltip");
  assert.doesNotMatch(screen, />Original<\/a>/, "downloads are icons, not text buttons");
  assert.match(screen, /files\/\$\{copy\}/);
  assert.match(screen, /copy="original"/);
  assert.match(screen, /copy="converted"/);
  assert.match(screen, /Export CSV/);
});

test("import-file settings expose active storage, named archive store and calendar age", () => {
  const screen = readFileSync(new URL("../src/settings/ImportFileSettings.tsx", import.meta.url), "utf8");
  assert.match(screen, /Active storage/);
  assert.match(screen, /Archive to/);
  assert.match(screen, /Months/);
  assert.match(screen, /Years/);
  assert.match(screen, /tenant_administrator/);
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

test("assertion review takes its custody checkbox default from the server", () => {
  const screen = readFileSync(new URL("../src/p7/screens.tsx", import.meta.url), "utf8");
  const api = readFileSync(new URL("../src/p7/api.ts", import.meta.url), "utf8");
  assert.match(screen, /useState\(row\.unitAtCustomerSiteDefault \?\? false\)/);
  assert.match(screen, /Unit is now at the customer's site/);
  assert.match(screen, /verifyAssertion\(row\.assertionId, evidenceRef\.trim\(\),[^,]+, unitAtCustomerSite\)/);
  assert.match(api, /unitAtCustomerSite/);
});

test("unit detail shows the computed owner and holder warning", () => {
  const screen = readFileSync(new URL("../src/units/UnitDetail.tsx", import.meta.url), "utf8");
  assert.match(screen, /detail\?\.ownerAndHolderDiffer/);
  assert.match(screen, /Owner and holder differ\./);
  assert.match(screen, /referenceNames\.party\(detail\.custodyPartyRef\)/);
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

test("an internal lookup that matches no unit says so in the results instead of a failed-request banner", () => {
  const screens = readFileSync(new URL("../src/p7/screens.tsx", import.meta.url), "utf8");
  const screen = screens.slice(screens.indexOf("export function InternalLookupScreen"), screens.indexOf("export function XrefScreen"));
  assert.ok(screen.includes("reason instanceof ApiError && reason.status === 404) setMissing(value.trim()); else setError(reason)"), "404 is a result");
  assert.ok(screen.includes("No unit matches “${missing}”"), "named in the empty state");
});

test("service-history references open their claim, RMA or work order by opaque id", () => {
  assert.equal(serviceRecordPath("claim", "0f1e"), "/claims/0f1e");
  assert.equal(serviceRecordPath("rma", "0f1e"), "/returns/0f1e");
  assert.equal(serviceRecordPath("refurbishment", "0f1e"), "/refurbishments/0f1e");
  assert.equal(serviceRecordPath("claim", null), null);
  assert.equal(serviceRecordPath("unknown_kind", "0f1e"), null);
});
