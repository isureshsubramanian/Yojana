import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import {
  OUTCOMES, REVIEW_KINDS, REVIEW_STATUSES, batchResult, batchStatusLabel, batchTotals, candidateLetter,
  canonicalisationStatusLabel, duration, factLabel, factValue, matchRuleLabel, mayAdministerMigration,
  mayReadMigration, outcomeLabel, profileClass, profileStatusLabel, profileTotals, refusalRuleLabel,
  reviewDecisionLabel, reviewDecisionOptions, reviewKindLabel, reviewStatusLabel, routeLabel, runtimeLabel,
  sampleExample, sampleFacts, serialAsStored, signedChange, templateResolutionLabel, templateStateLabel,
  templateStateTone, templateTermsLabel,
} from "../src/integration/migrationModel.ts";
import type { MigrationBatch, MigrationProfile, MigrationReviewDetail, TemplateSourceTerms } from "../src/integration/migrationTypes.ts";

const consoleScreen = readFileSync(new URL("../src/integration/MigrationConsole.tsx", import.meta.url), "utf8");
const profilingScreen = readFileSync(new URL("../src/integration/MigrationProfiling.tsx", import.meta.url), "utf8");
const reviewScreen = readFileSync(new URL("../src/integration/MigrationReview.tsx", import.meta.url), "utf8");
const templateScreen = readFileSync(new URL("../src/integration/MigrationTemplates.tsx", import.meta.url), "utf8");
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

test("migration routes and the Integration-area link admit administrators and Migration leads", () => {
  assert.match(routes, /integration\/migration.*MigrationConsole/);
  assert.match(routes, /integration\/migration\/profiling.*MigrationProfiling/);
  assert.match(routes, /integration\/migration\/review.*MigrationReview/);
  assert.match(routes, /integration\/migration\/templates.*MigrationTemplates/);
  assert.match(integrationTabs, /\["\/integration\/migration", "Migration"\]/);
  assert.match(integrationTabs, /mayReadMigration\(roles\)/);
  assert.match(common, /Tenant administrator or Migration lead access is required/);
  assert.equal(mayReadMigration(["tenant_administrator"]), true);
  assert.equal(mayReadMigration(["migration_lead"]), true);
  assert.equal(mayReadMigration(["integration_operator"]), false);
  assert.equal(mayAdministerMigration(["migration_lead"]), false);
  assert.equal(mayAdministerMigration(["tenant_administrator"]), true);
});

test("review queue is enabled while later migration tabs remain disabled", () => {
  for (const title of ["Arrives with P9-T08", "Arrives with P9-T11"]) assert.match(common, new RegExp(title));
  assert.match(common, /aria-disabled="true"/);
  assert.match(common, /\/integration\/migration\/review", "Review queue"/);
  assert.match(common, /\/integration\/migration\/templates", "Contract templates"/);
  assert.match(common, /templateSummary/); assert.match(common, /needsPerson\.templates/);
  assert.doesNotMatch(common, /Review queue", "Arrives/);
});

test("migration API adapter covers the merged endpoints and sends no browser actor", () => {
  for (const fragment of ["/sources", "/licence", "/test", "/batches", "/profile", "/samples", "/profiling/outcomes", "/canonicalisation", "/canonicalise", "/review"]) assert.match(api, new RegExp(fragment.replace("/", "\\/")));
  assert.match(api, /method: "POST", body: \{ sourceId \}/);
  assert.match(api, /method: "PUT", body: \{ classes, expectedVersion \}/);
  assert.doesNotMatch(api, /actor|requestedBy|confirmedBy/i);
});

test("review screen maps every answered mockup element to paged API data", () => {
  for (const text of ["Review queue", "Waiting", "Merged", "Kept apart", "Fix in the incumbent", "Carried over from earlier extractions", "Serial as stored", "Rule tried", "Candidates", "Suggested", "Any item family", "All statuses"]) assert.ok(reviewScreen.includes(text), text);
  assert.match(reviewScreen, /<DataGrid/); assert.match(reviewScreen, /<KpiStrip/); assert.match(reviewScreen, /<Rail/);
  assert.match(reviewScreen, /nextCursor/); assert.match(reviewScreen, /keyset paged/);
  assert.match(reviewScreen, /migrationApi\.canonicalisation/); assert.match(reviewScreen, /migrationApi\.review\(/); assert.match(reviewScreen, /migrationApi\.reviewDetail/);
  assert.doesNotMatch(reviewScreen, /label: "Decided", value:/);
  assert.doesNotMatch(reviewScreen, /same item.*across items/i);
});

test("Migration leads see read screens and decisions, but administrator actions stay hidden", () => {
  assert.match(consoleScreen, /mayAdministerMigration\(session\.roles\)/);
  assert.match(consoleScreen, /const actions = admin \?/);
  assert.match(consoleScreen, /admin && source && <LicenceBanner/);
  assert.match(consoleScreen, /if \(!admin\) return;.*loadSources/s);
  assert.match(profilingScreen, /const actions = admin \?/);
  assert.match(reviewScreen, /admin && <button/); assert.match(reviewScreen, /Run canonicalisation/);
  assert.match(reviewScreen, /migrationApi\.decideReview/);
  assert.match(templateScreen, /const actions = admin \?/);
  assert.match(templateScreen, /Keyword rules/); assert.match(templateScreen, /Propose again/);
  assert.match(templateScreen, /Approve \{pattern\.templateCount/);
  assert.match(templateScreen, /Approve this template/);
});

test("contract templates screen maps every approved mockup element to server data", () => {
  for (const text of ["Contract templates", "Patterns", "Templates in", "Nothing is loaded from a template until it is approved", "Templates", "Approved", "Waiting for approval", "Needs a person", "Policies to create", "All source templates", "Deleted in source"]) assert.ok(templateScreen.includes(text), text);
  assert.match(templateScreen, /<DataGrid/g); assert.match(templateScreen, /<KpiStrip/); assert.match(templateScreen, /<Rail/);
  assert.match(templateScreen, /templatePatterns/); assert.match(templateScreen, /templateSummary/); assert.match(templateScreen, /approveTemplatePattern/);
  assert.match(templateScreen, /nextCursor/); assert.match(templateScreen, /keyset paged/);
  assert.doesNotMatch(templateScreen, />\{(?:row|detail)\.(?:templateKey|templateSourceKey|extractionBatchId)\}</);
});

test("template drawer edits one readable policy choice per block and keeps the pattern warning", () => {
  for (const text of ["As stored in the source", "Wording to read", "Cover blocks", "Policy choice", "Create a new policy from these terms", "Add a block", "Remove block", "Approve this template", "Save without approving", "Changing a block moves this template out of its pattern"]) assert.ok(templateScreen.includes(text), text);
  assert.match(templateScreen, /catalogue\.policies\.filter\(policy => policy\.familyCode === block\.familyCode\)/);
  assert.match(templateScreen, /updateTemplate\(detail\.templateKey, blocks, detail\.version\)/);
  assert.match(templateScreen, /approveTemplate\(detail\.templateKey\)/);
  assert.match(templateScreen, /assumed: no unit in the source/);
});

test("keyword rules are an administrator-only tenant-setting form", () => {
  assert.match(templateScreen, /admin \? <>.*showKeywordRules.*proposeAgain/s);
  assert.match(templateScreen, /Rules are a tenant setting/);
  assert.match(templateScreen, /Add keyword/); assert.match(templateScreen, /Save keyword rules/);
  assert.match(templateScreen, /saveTemplateKeywordRules\(rules, version\)/);
  assert.doesNotMatch(api, /actor|approvedBy|requestedBy/i);
});

test("template states, resolutions, refusals and source terms are always readable", () => {
  for (const state of ["waiting", "needs_person", "approved", "waiting_for_policy_publication"] as const) {
    assert.doesNotMatch(templateStateLabel(state), /_/); assert.ok(templateStateTone(state));
  }
  for (const resolution of ["existing_policy", "policy_to_create"] as const) assert.doesNotMatch(templateResolutionLabel(resolution), /_/);
  for (const rule of ["template_mapping_needs_review", "template_pattern_not_draft", "template_policy_terms_incomplete", "template_mapping_changed", "template_keyword_duplicate"]) assert.doesNotMatch(refusalRuleLabel(rule), /_/);
  const terms: TemplateSourceTerms = { durationValue: 36, durationUnit: "months", durationUnitVerified: false, partsPct: 100, labourPct: 100, travelPct: 0, responseHours: 24, responseSource: "1 day", coverageHours: "mon=0800-1700;fri=0800-1700", contractType: "serial" };
  assert.equal(templateTermsLabel(terms), "36 months (assumed) · parts included, labour included, travel excluded · response 1 day (read as 24 hours) · Mon 08:00–17:00, Fri 08:00–17:00");
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

const reviewDetail: MigrationReviewDetail = {
  itemId: "review-internal", kind: "same_serial_one_item", status: "waiting", serialAsStored: " rs-101 ",
  itemCode: "RS-1100", itemName: "Rack server", ruleTried: "trimmed_upper", suggestion: "merge_into",
  suggestedKeepSourceKey: "candidate-a", suggestionReason: "A has the stronger lineage.", hint: null,
  candidates: [
    { sourceKey: "candidate-a", serialAsStored: "RS-101", itemCode: "RS-1100", itemName: "Rack server", transactions: 12, firstReceipt: "2026-09-01", lastDelivery: "2026-09-03", lastDeliveryParty: "Asterra Compute Works", hasEquipmentCard: true, hasContract: true, disposition: "review" },
    { sourceKey: "candidate-b", serialAsStored: " rs-101 ", itemCode: "RS-1100", itemName: "Rack server", transactions: 1, firstReceipt: "2026-09-01", lastDelivery: null, lastDeliveryParty: null, hasEquipmentCard: false, hasContract: false, disposition: "review" },
  ],
  earlierDecisions: [],
};

test("drawer decision options offer each same-item merge target and never offer a cross-item merge", () => {
  const sameItem = reviewDecisionOptions(reviewDetail);
  assert.deepEqual(sameItem.map(option => option.label), ["Merge into A", "Merge into B", "Keep apart", "Fix in the incumbent"]);
  assert.equal(sameItem[0]!.suggested, true);
  const crossItem = reviewDecisionOptions({ ...reviewDetail, kind: "same_serial_different_items", candidates: reviewDetail.candidates.map((candidate, index) => ({ ...candidate, itemCode: index ? "DS-24" : "RS-1100" })) });
  assert.deepEqual(crossItem.map(option => option.label), ["Keep apart", "Fix in the incumbent"]);
  const orphanWithoutHint = reviewDecisionOptions({ ...reviewDetail, kind: "orphan_equipment_card", hint: null });
  assert.doesNotMatch(orphanWithoutHint.map(option => option.label).join(" "), /Attach/);
  const orphanWithHint = reviewDecisionOptions({ ...reviewDetail, kind: "orphan_equipment_card", hint: "Nearest same-item serial is one edit away." });
  assert.deepEqual(orphanWithHint.map(option => option.label), ["Attach to A", "Keep apart", "Fix in the incumbent"]);
});

test("drawer requires a reason, requires a correction note, and keeps refusal rules readable", () => {
  assert.match(reviewScreen, /!!reason\.trim\(\)/);
  assert.match(reviewScreen, /decision !== "fix_in_incumbent" \|\| !!note\.trim\(\)/);
  assert.match(reviewScreen, /const editable = detail\.status === "waiting" && !saved/);
  assert.match(reviewScreen, /Decision saved\. This item now has a final decision/);
  assert.match(reviewScreen, /className="form-error" role="alert"/);
  assert.match(reviewScreen, /Earlier decisions/); assert.match(reviewScreen, /Carried over to this extraction/); assert.match(reviewScreen, /Earlier extraction · not applied/);
  for (const rule of ["migration_review_reason_required", "migration_review_note_required", "migration_review_keep_candidate_invalid", "migration_cross_item_merge_forbidden", "migration_canonicalisation_profile_blocked"]) assert.doesNotMatch(refusalRuleLabel(rule), /_/);
  assert.equal(refusalRuleLabel("migration_cross_item_merge_forbidden"), "Merge across items is not allowed");
});

test("review catalogue and pure formatting never expose stored codes or candidate keys", () => {
  for (const kind of REVIEW_KINDS) assert.doesNotMatch(reviewKindLabel(kind), /_/);
  for (const status of REVIEW_STATUSES) assert.doesNotMatch(reviewStatusLabel(status), /_/);
  for (const decision of ["merge_into", "keep_apart", "fix_in_incumbent"] as const) assert.doesNotMatch(reviewDecisionLabel(decision), /_/);
  for (const rule of ["exact", "trimmed_upper", "family_normalised", "reviewed", "no_serial_match"]) assert.doesNotMatch(matchRuleLabel(rule), /_/);
  for (const status of ["queued", "running", "completed", "failed", "not_run"]) assert.doesNotMatch(canonicalisationStatusLabel(status), /_/);
  assert.equal(serialAsStored(" RS-101 "), '" RS-101 "');
  assert.equal(serialAsStored("RS-101"), "RS-101");
  assert.equal(serialAsStored("RS\t101"), '"RS\\t101"');
  assert.equal(serialAsStored(null), "No serial recorded");
  assert.equal(candidateLetter(0), "A"); assert.equal(candidateLetter(25), "Z"); assert.equal(candidateLetter(26), "AA");
  assert.doesNotMatch(reviewScreen, />\{candidate\.sourceKey\}</);
});
