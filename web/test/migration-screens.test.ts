import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import {
  OUTCOMES, batchResult, batchStatusLabel, batchTotals, duration, factLabel, factValue, outcomeLabel,
  profileClass, profileStatusLabel, profileTotals, routeLabel, runtimeLabel, sampleExample, sampleFacts,
  signedChange,
} from "../src/integration/migrationModel.ts";
import type { MigrationBatch, MigrationProfile } from "../src/integration/migrationTypes.ts";

const consoleScreen = readFileSync(new URL("../src/integration/MigrationConsole.tsx", import.meta.url), "utf8");
const profilingScreen = readFileSync(new URL("../src/integration/MigrationProfiling.tsx", import.meta.url), "utf8");
const common = readFileSync(new URL("../src/integration/MigrationCommon.tsx", import.meta.url), "utf8");
const api = readFileSync(new URL("../src/integration/migrationApi.ts", import.meta.url), "utf8");
const routes = readFileSync(new URL("../src/routes.tsx", import.meta.url), "utf8");
const integrationTabs = readFileSync(new URL("../src/integration/Common.tsx", import.meta.url), "utf8");

const batch: MigrationBatch = {
  batchId: "internal-only", sourceId: "source-internal", sourceSystem: "source-one", kind: "full",
  requestedByName: "R. Okafor", requestedAt: "2026-09-01T08:00:00Z", startedAt: "2026-09-01T08:00:00Z",
  finishedAt: "2026-09-01T09:02:03Z", status: "succeeded", failure: null,
  tables: [
    { table: "SRC_SERIALS", role: "serial_master", label: "Serial master", rowsRead: 20, inserted: 2, updated: 3, unchanged: 15, absent: 1, durationMilliseconds: 62_000 },
    { table: "SRC_ITEMS", role: "item_serial_settings", label: "Item serial settings", rowsRead: 5, inserted: 0, updated: 1, unchanged: 4, absent: 0, durationMilliseconds: 3_000 },
  ],
};

const profile: MigrationProfile = {
  runId: "run-internal", batchId: batch.batchId, startedAt: batch.finishedAt, finishedAt: "2026-09-01T09:03:00Z", status: "blocked",
  classes: [
    { code: "negative_quantity", name: "Negative quantity", outcome: "stop_load", outcomeLabel: "Stop the load", count: 2, change: -1, note: null },
    { code: "duplicate_serial_same_item", name: "Same serial on one item, twice", outcome: "needs_person", outcomeLabel: "Go to a person", count: 5, change: 2, note: null },
    { code: "serials_in_user_fields", name: "Serials hiding in user fields", outcome: "needs_person", outcomeLabel: "Go to a person", count: null, change: null, note: "Not configured: no approved rule." },
  ],
};

test("migration routes and the Integration-area link are registered for tenant administrators", () => {
  assert.match(routes, /integration\/migration.*MigrationConsole/);
  assert.match(routes, /integration\/migration\/profiling.*MigrationProfiling/);
  assert.match(integrationTabs, /\["\/integration\/migration", "Migration"\]/);
  assert.match(integrationTabs, /roles\.includes\("tenant_administrator"\)/);
  assert.match(common, /Tenant administrator access is required/);
});

test("future migration tabs are disabled in place and name their delivery task", () => {
  for (const title of ["Arrives with P9-T05", "Arrives with P9-T08", "Arrives with P9-T11"]) assert.match(common, new RegExp(title));
  assert.match(common, /aria-disabled="true"/);
  assert.doesNotMatch(common, /Review queue.*to=/);
});

test("migration API adapter covers the merged endpoints and sends no browser actor", () => {
  for (const fragment of ["/sources", "/licence", "/test", "/batches", "/profile", "/samples", "/profiling/outcomes"]) assert.match(api, new RegExp(fragment.replace("/", "\\/")));
  assert.match(api, /method: "POST", body: \{ sourceId \}/);
  assert.match(api, /method: "PUT", body: \{ classes, expectedVersion \}/);
  assert.doesNotMatch(api, /actor|requestedBy|confirmedBy/i);
});

test("console uses the house grid, KPI, drawer, source test and licence form", () => {
  assert.match(consoleScreen, /<DataGrid/); assert.match(consoleScreen, /<KpiStrip/); assert.match(consoleScreen, /<Rail/);
  assert.match(consoleScreen, /Test the source/); assert.match(consoleScreen, /Start a full extraction/);
  assert.match(consoleScreen, /Change or withdraw/); assert.match(consoleScreen, /Withdraw confirmation/);
  assert.match(consoleScreen, /migrationApi\.testSource/); assert.match(consoleScreen, /migrationApi\.licence/);
  assert.match(consoleScreen, /migrationApi\.startBatch/); assert.match(consoleScreen, /startNotice\.startsWith\("Extraction refused"\)/);
  assert.match(consoleScreen, /requestedByName/);
  assert.doesNotMatch(consoleScreen, /Matched so far|Cutover target|Waiting for a person/);
});

test("profiling screen has batch paging, blocked warning, filters and sample drawer", () => {
  assert.match(profilingScreen, /Older extractions/); assert.match(profilingScreen, /batches\.onPage/);
  assert.match(profilingScreen, /The load is stopped:/); assert.match(profilingScreen, /migration-outcome-chips/);
  assert.match(profilingScreen, /<DataGrid/); assert.match(profilingScreen, /<KpiStrip/); assert.match(profilingScreen, /<Rail/);
  assert.match(profilingScreen, /migrationApi\.samples/); assert.match(profilingScreen, /Showing \{samples\.length/);
  assert.doesNotMatch(profilingScreen, /JSON\.stringify/);
});

test("outcome editor fixes hard stops and keeps server refusals visible", () => {
  assert.match(profilingScreen, /const fixed = labels\.hardStop === true/);
  assert.match(profilingScreen, /disabled=\{fixed\}/);
  assert.match(profilingScreen, /Fixed at Stop the load because/);
  assert.match(profilingScreen, /migrationApi\.saveOutcomes\(classes, version\)/);
  assert.match(profilingScreen, /catch \(reason\) \{ setError\(reason\); \}/);
  assert.match(profilingScreen, /<ErrorNote error=\{error\}/);
});

test("catalogue gives readable labels for every current server code", () => {
  for (const value of OUTCOMES) assert.doesNotMatch(outcomeLabel(value), /_/);
  for (const value of ["queued", "running", "succeeded", "failed", "refused"]) assert.doesNotMatch(batchStatusLabel(value), /_/);
  for (const value of ["queued", "running", "completed", "blocked", "failed"]) assert.doesNotMatch(profileStatusLabel(value), /_/);
  for (const row of profile.classes) assert.doesNotMatch(profileClass(row.code).label, /_/);
  assert.equal(runtimeLabel("sql_server"), "SQL Server"); assert.equal(routeLabel("separate"), "Separate connection");
});

test("durations and changes use concise signed display formatting", () => {
  assert.equal(duration(0), "0 s"); assert.equal(duration(62_000), "1 m 2 s"); assert.equal(duration(3_723_000), "1 h 2 m 3 s");
  assert.equal(signedChange(12), "+12"); assert.equal(signedChange(-5), "−5"); assert.equal(signedChange(0), "0"); assert.equal(signedChange(null), "—");
});

test("sample facts render as labelled values rather than raw JSON", () => {
  const sample = { sourceKey: "row-1", serial: "SRV-001", item: "RS-1100", facts: { warehouse: "Pune Main", total_quantity: "2", match_rule: "trimmed_upper_case" } };
  assert.equal(sampleExample(sample), "SRV-001 · RS-1100 · Warehouse: Pune Main · Total quantity: 2");
  assert.deepEqual(sampleFacts(sample), [["Warehouse", "Pune Main"], ["Total quantity", "2"], ["Match rule", "Trimmed and upper-cased"]]);
  assert.equal(factLabel("last_delivery_customer"), "Last delivery customer"); assert.equal(factValue("per_warehouse_quantity"), "Per-warehouse quantity");
});

test("batch totals and result text use only fields returned by batch detail", () => {
  assert.deepEqual(batchTotals(batch.tables!), { rowsRead: 25, inserted: 2, updated: 4, unchanged: 19, absent: 1, durationMilliseconds: 65_000 });
  assert.equal(batchResult(batch), "25 rows · 2 new · 4 changed · 1 gone");
  assert.match(batchResult({ ...batch, status: "refused", failure: "migration_extraction_licence_unconfirmed", tables: null }), /Licence position is not confirmed/);
});

test("profile totals preserve outcomes and derive a previous figure only when the API gives every change", () => {
  assert.deepEqual(profileTotals(profile), { flagged: 7, previous: 6, outcomes: { stop_load: 2, needs_person: 5, fixed_by_rule: 0, loaded_with_note: 0 } });
  assert.equal(profileTotals({ ...profile, classes: profile.classes.map(row => ({ ...row, change: null })) }).previous, null);
});
