import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import {
  OUTCOMES, REVIEW_KINDS, REVIEW_STATUSES, batchLabel, batchResult, batchStatusLabel, batchTotals, candidateLetter,
  canonicalisationStatusLabel, countTotal, duration, factLabel, factValue, finalLoadDisabledReason, loadStatusLabel,
  loadStatusTone, matchRuleLabel, mayAdministerMigration, phaseStatusLabel, phaseStatusTone,
  mayPrepareValidation, mayReadMigration, mayReviewValidation, outcomeLabel, percentage, profileClass, profileStatusLabel, profileTotals, refusalRuleLabel,
  refusalReasonLabel, reviewDecisionLabel, reviewDecisionOptions, reviewKindLabel, reviewStatusLabel, rollbackOutcomeLabel, routeLabel, runtimeLabel,
  sampleExample, sampleFacts, serialAsStored, signoffDisabledReason, signedChange, templateResolutionLabel, templateStateLabel,
  templateStateTone, templateTermsLabel, validationAttentionCount, validationCheckLabel, validationDifferenceLabel,
  validationLimitLabel, validationResultLabel, validationRoleLabel, validationTrail, validationTrailLine,
} from "../src/integration/migrationModel.ts";
import type { MigrationApply, MigrationBatch, MigrationProfile, MigrationReviewDetail, MigrationValidation, TemplateSourceTerms } from "../src/integration/migrationTypes.ts";

const consoleScreen = readFileSync(new URL("../src/integration/MigrationConsole.tsx", import.meta.url), "utf8");
const profilingScreen = readFileSync(new URL("../src/integration/MigrationProfiling.tsx", import.meta.url), "utf8");
const reviewScreen = readFileSync(new URL("../src/integration/MigrationReview.tsx", import.meta.url), "utf8");
const templateScreen = readFileSync(new URL("../src/integration/MigrationTemplates.tsx", import.meta.url), "utf8");
const validationScreen = readFileSync(new URL("../src/integration/MigrationValidation.tsx", import.meta.url), "utf8");
const refusalsScreen = readFileSync(new URL("../src/integration/MigrationRefusals.tsx", import.meta.url), "utf8");
const common = readFileSync(new URL("../src/integration/MigrationCommon.tsx", import.meta.url), "utf8");
const api = readFileSync(new URL("../src/integration/migrationApi.ts", import.meta.url), "utf8");
const routes = readFileSync(new URL("../src/routes.tsx", import.meta.url), "utf8");
const integrationTabs = readFileSync(new URL("../src/integration/Common.tsx", import.meta.url), "utf8");
const gapReportScreen = readFileSync(new URL("../src/entitlements/GapReport.tsx", import.meta.url), "utf8");

const batch: MigrationBatch = {
  batchId: "internal-only", displayNumber: "MB-0007", sourceId: "source-internal", sourceSystem: "source-one", kind: "full",
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
  assert.match(routes, /integration\/migration\/validation.*MigrationValidation/);
  assert.match(routes, /integration\/migration\/loads\/:loadId\/refusals.*MigrationRefusals/);
  assert.match(integrationTabs, /\["\/integration\/migration", "Migration"\]/);
  assert.match(integrationTabs, /mayReadMigration\(roles\)/);
  assert.match(common, /Tenant administrator or Migration lead access is required/);
  assert.equal(mayReadMigration(["tenant_administrator"]), true);
  assert.equal(mayReadMigration(["migration_lead"]), true);
  assert.equal(mayReadMigration(["integration_operator"]), false);
  assert.equal(mayAdministerMigration(["migration_lead"]), false);
  assert.equal(mayAdministerMigration(["tenant_administrator"]), true);
});

test("validation is enabled while the later migration tab remains disabled", () => {
  assert.match(common, /Arrives with P9-T11/);
  assert.doesNotMatch(common, /Arrives with P9-T08/);
  assert.match(common, /aria-disabled="true"/);
  assert.match(common, /\/integration\/migration\/review", "Review queue"/);
  assert.match(common, /\/integration\/migration\/templates", "Contract templates"/);
  assert.match(common, /\/integration\/migration\/validation", "Validation and sign-off"/);
  assert.match(common, /validationAttentionCount/);
  assert.match(common, /templateSummary/); assert.match(common, /needsPerson\.templates/);
  assert.doesNotMatch(common, /Review queue", "Arrives/);
});

test("migration API adapter covers the merged endpoints and sends no browser actor", () => {
  for (const fragment of ["/sources", "/licence", "/test", "/batches", "/profile", "/samples", "/profiling/outcomes", "/canonicalisation", "/canonicalise", "/reconstruction", "/map", "/apply", "/loads/", "/refusals", "/refusals/export.csv", "/rollback", "/rollback/touched-units", "/review", "/review/export.csv", "/canonicalisation/families/catalogue", "/validate", "/validation/checks", "/differences", "/explain", "/sign", "/sample", "/trace", "/signoff-record", "/validation/settings"]) assert.match(api, new RegExp(fragment.replace("/", "\\/")));
  assert.match(api, /method: "POST", body: \{ sourceId \}/);
  assert.match(api, /method: "PUT", body: \{ classes, expectedVersion \}/);
  assert.doesNotMatch(api, /actor|requestedBy|confirmedBy/i);
});

const validationRun: MigrationValidation = {
  runId: "run-internal", batchId: "batch-internal", mapRunId: "map-internal", targetKind: "module",
  rehearsal: false, rehearsalNumber: null, status: "completed", startedAt: "2026-09-01T08:00:00Z", finishedAt: "2026-09-01T08:01:00Z",
  loadDate: "2026-09-01T07:55:00Z", signoffRecordId: null, signoffRehearsal: null,
  signoffCreatedAt: null, signoffCreatedByName: null, hasFinalSignoff: false, failureCode: null, failureDetail: null,
  checksTotal: 3, passed: 1, explained: 0, failed: 2, hardFailed: 1, signedChecks: 1,
  units: 409_118, events: 3_611_402, eventsShortPercent: 0.04, sampleTraced: 199, sampleTotal: 200,
  hardFailSummary: "One hard fail.", checks: [
    { checkId: "check-unit", code: "unit_master", label: "Unit master count", limitKind: "zero", sourceTotal: 409_118, targetTotal: 409_118, difference: 0, result: "pass", explanation: null, explainedByName: null, explainedAt: null, howCounted: "After review merges.", ranAt: "2026-09-01T08:00:00Z", durationMilliseconds: 1200, signatures: [{ role: "preparer", actorName: "Anika Iyer", signedAt: "2026-09-01T08:10:00Z", superseded: false, supersededAt: null }, { role: "reviewer", actorName: "Tomas Lindqvist", signedAt: "2026-09-01T08:20:00Z", superseded: false, supersededAt: null }] },
    { checkId: "check-hard", code: "negative_quantity", label: "Negative quantity", limitKind: "none", sourceTotal: 1, targetTotal: null, difference: null, result: "hard_fail", explanation: null, explainedByName: null, explainedAt: null, howCounted: "Source fact.", ranAt: "2026-09-01T08:00:00Z", durationMilliseconds: 10, signatures: [] },
    { checkId: "check-sample", code: "stratified_sample", label: "Stratified sample", limitKind: "all", sourceTotal: 200, targetTotal: 199, difference: -1, result: "fail", explanation: null, explainedByName: null, explainedAt: null, howCounted: "By family and age.", ranAt: "2026-09-01T08:00:00Z", durationMilliseconds: 90, signatures: [] },
  ], gapsByKind: [
    { kind: "card_cover_policy_not_set", label: "Equipment-card cover policy not set", count: 12 },
    { kind: "serial_master_cover_policy_not_set", label: "Serial-master cover policy not set", count: 5 },
  ],
};

test("validation screen maps fixture fields into the approved grid and KPI structure", () => {
  for (const text of ["Checks", "Signed off", "Units loaded", "History events", "Sample traced", "Source", "Difference", "Prepared · reviewed", "Signed"]) assert.ok(validationScreen.includes(text), text);
  assert.match(validationScreen, /<DataGrid/); assert.match(validationScreen, /<KpiStrip/); assert.match(validationScreen, /<Rail/);
  for (const field of ["checksTotal", "signedChecks", "units", "events", "eventsShortPercent", "sampleTraced", "sampleTotal", "sourceTotal", "targetTotal", "difference"]) assert.match(validationScreen, new RegExp(`run\\.${field}|check\\.${field}`));
  assert.equal(validationRun.units.toLocaleString("en"), "409,118");
  assert.equal(percentage(validationRun.eventsShortPercent), "0.04%");
  assert.equal(batchLabel(batch), "Extraction MB-0007");
  assert.equal(batchLabel(batch, true), "Latest extraction · MB-0007");
  for (const screen of [consoleScreen, profilingScreen, reviewScreen, templateScreen, validationScreen]) assert.match(screen, /batchLabel/);
  assert.match(validationScreen, /load date/);
});

test("map validation and load views use server-labelled links into the paged plan-gap filter", () => {
  for (const field of ["row.kind", "row.label", "row.count"]) {
    assert.match(consoleScreen, new RegExp(field.replace(".", "\\.")));
    assert.match(validationScreen, new RegExp(field.replace(".", "\\.")));
  }
  assert.match(consoleScreen, /gapKind=.*row\.kind/);
  assert.match(validationScreen, /migrationApi\.mapGaps\(batchId, selected, cursor\)/);
  assert.match(validationScreen, /Plan gaps/);
  assert.match(validationScreen, /keyset-paged/);
  assert.match(validationScreen, /Policy gaps are warnings and do not block sign-off on their own/);
  assert.doesNotMatch(validationScreen, />\{row\.planUnitId\}</);
  assert.deepEqual(validationRun.gapsByKind.map(row => [row.label, row.count]), [
    ["Equipment-card cover policy not set", 12], ["Serial-master cover policy not set", 5],
  ]);
});

test("hard failures show the approved cutover warning and final sign-off reason", () => {
  assert.match(validationScreen, /hard fail.*the cutover/s);
  assert.match(validationScreen, /Fix them in the incumbent; the next extraction and rehearsal will check again/);
  assert.equal(signoffDisabledReason(validationRun), "1 hard fail and 2 checks are not signed");
  assert.match(validationScreen, /title=\{disabledReason/);
});

test("rehearsal sign-off bypasses the final gate and carries a visible stamp", () => {
  assert.equal(signoffDisabledReason({ ...validationRun, rehearsal: true, rehearsalNumber: 3 }), null);
  assert.match(validationScreen, /record\.rehearsal \? "REHEARSAL" : "FINAL"/);
  assert.match(validationScreen, /Creates a REHEARSAL record/);
  assert.match(validationScreen, /signoffExportUrl/);
  assert.match(validationScreen, /Rehearsal R/);
  assert.match(validationScreen, /validationExportUrl\(batchId, "csv"\)/);
  assert.match(validationScreen, /validationExportUrl\(batchId, "xlsx"\)/);
  assert.match(validationScreen, />Excel</);
});

test("a sign-off returned by a reload keeps the action disabled and the CSV available", () => {
  const reloaded = { ...validationRun, signoffRecordId: "signoff-internal", signoffRehearsal: false,
    signoffCreatedAt: "2026-09-01T08:30:00Z", signoffCreatedByName: "Anika Iyer" };
  assert.equal(signoffDisabledReason(reloaded), "A sign-off record exists for this run");
  assert.match(validationScreen, /run\?\.signoffRecordId/);
  assert.match(validationScreen, /migrationApi\.signoffExportUrl\(signoffRecordId\)/);
});

test("validation actions follow preparer and reviewer role gates", () => {
  assert.equal(mayPrepareValidation(["migration_lead"]), true);
  assert.equal(mayPrepareValidation(["tenant_administrator"]), true);
  assert.equal(mayReviewValidation(["migration_lead"]), false);
  assert.equal(mayReviewValidation(["tenant_administrator"]), true);
  assert.match(validationScreen, /prepare && !hasSignature.*Sign as preparer/s);
  assert.match(validationScreen, /review && !hasSignature.*Sign as reviewer/s);
  assert.match(validationScreen, /const actions = admin \?/);
});

test("signature refusals and explain rules stay visible and readable", () => {
  assert.match(validationScreen, /migrationApi\.signValidationCheck/);
  assert.match(validationScreen, /className="form-error" role="alert"/);
  assert.equal(refusalRuleLabel("migration_validation_distinct_signers_required"), "The preparer and reviewer must be different people");
  assert.match(validationScreen, /check\.result === "fail" && <Section title="Explain this difference"/);
  assert.doesNotMatch(validationScreen, /check\.result === "hard_fail" && <Section title="Explain/);
  assert.match(validationScreen, /!explanation\.trim\(\)/);
});

test("sample tracing is keyset-paged and requires a mismatch note", () => {
  for (const text of ["Sample tracing", "Source trail", "Plan or module events", "Traced", "Mismatch", "Mismatch note"]) assert.ok(validationScreen.includes(text), text);
  assert.match(validationScreen, /validationSample\(runId, cursor\)/);
  assert.match(validationScreen, /disabled=\{busy \|\| !note\.trim\(\)\}/);
  assert.match(validationScreen, /traceValidationSample/);
  assert.match(validationScreen, /page\.nextCursor/);
});

test("validation catalogue formats every current code without snake case", () => {
  for (const code of ["unit_master", "active_cover", "open_service_calls", "stock_by_item_warehouse", "serial_in_multiple_warehouses", "negative_quantity", "historical_events", "status_histogram", "event_per_unit", "one_current_row", "signed_quantity", "stratified_sample", "cover_gap_report", "attachments_by_hash"]) assert.doesNotMatch(validationCheckLabel(code), /_/);
  for (const result of ["pass", "explained", "fail", "hard_fail", "report"] as const) assert.doesNotMatch(validationResultLabel(result), /_/);
  for (const limit of ["zero", "none", "all", "< 0.1%"]) assert.doesNotMatch(validationLimitLabel(limit), /_/);
  for (const role of ["preparer", "reviewer"] as const) assert.doesNotMatch(validationRoleLabel(role), /_/);
  for (const kind of ["item_warehouse", "status_bucket", "serial_quantity"]) assert.doesNotMatch(validationDifferenceLabel(kind), /_/);
  for (const rule of ["migration_validation_hard_fail_not_signable", "migration_validation_distinct_signers_required", "migration_validation_explanation_required", "migration_signoff_hard_fail", "migration_signoff_unsigned_checks"]) assert.doesNotMatch(refusalRuleLabel(rule), /_/);
});

test("validation attention and trail formatting are pure fixture-driven values", () => {
  assert.equal(validationAttentionCount(validationRun), 2);
  const json = '[{"effectiveDate":"2026-09-01T00:00:00","movementKind":"goods_receipt","documentNumber":70110,"quantity":1,"warehouse":"Pune Main"}]';
  const events = validationTrail(json);
  assert.equal(events.length, 1);
  assert.equal(validationTrailLine(events[0]!), "2026-09-01 · Goods receipt · document 70110 · quantity 1 · warehouse Pune Main");
  assert.deepEqual(validationTrail("not json"), []);
});

test("review screen maps every answered mockup element to paged API data", () => {
  for (const text of ["Review queue", "Waiting", "Decided", "Merged candidate rows", "Carried over from earlier extractions", "Serial as stored", "Rule tried", "Candidates", "Suggested", "Any item family", "All statuses", "Serial or item code", "CSV"]) assert.ok(reviewScreen.includes(text), text);
  assert.match(reviewScreen, /<DataGrid/); assert.match(reviewScreen, /<KpiStrip/); assert.match(reviewScreen, /<Rail/);
  assert.match(reviewScreen, /nextCursor/); assert.match(reviewScreen, /keyset paged/);
  assert.match(reviewScreen, /migrationApi\.canonicalisation/); assert.match(reviewScreen, /migrationApi\.review\(/); assert.match(reviewScreen, /migrationApi\.reviewDetail/);
  assert.match(reviewScreen, /migrationApi\.canonicalFamilyCatalogue/); assert.match(reviewScreen, /reviewExportUrl/);
  assert.match(reviewScreen, /waitingReviewItemsByKind/); assert.match(reviewScreen, /decidedReviewItemsByDecision/);
  assert.match(reviewScreen, /find: find\.trim\(\)/);
});

test("review rows and lineage use server-supplied readable labels", () => {
  assert.match(reviewScreen, /Merge into \{row\.suggestedKeepLabel/);
  assert.match(reviewScreen, /candidate\.cardNumber/); assert.match(reviewScreen, /candidate\.contractNumber/);
  assert.match(reviewScreen, /candidate\.receiptDocumentNumber/); assert.match(reviewScreen, /candidate\.receiptDocumentLine/);
  assert.doesNotMatch(reviewScreen, />\{row\.suggestedKeepSourceKey\}</);
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

test("console phase cards read every real phase for the selected extraction", () => {
  for (const label of ["2 · Canonicalise", "3 · Reconstruct", "4 · Map", "5 · Validate", "6 · Load"]) assert.ok(consoleScreen.includes(label), label);
  for (const call of ["profile", "canonicalisation", "reconstruction", "map", "validation", "load"]) assert.match(consoleScreen, new RegExp(`migrationApi\\.${call}\\(phaseBatchId`));
  for (const field of ["candidateRowsTotal", "waitingReviewItems", "candidatesReconstructed", "eventsByMovementKind", "queuedForReviewByReason", "unitsByOrigin", "gapsByKind", "hardFailed", "signedChecks", "checksTotal"]) assert.ok(consoleScreen.includes(field), field);
  assert.match(consoleScreen, /\/integration\/migration\/profiling/);
  assert.match(consoleScreen, /\/integration\/migration\/review/);
  assert.match(consoleScreen, /\/integration\/migration\/validation/);
  assert.doesNotMatch(consoleScreen, /Not built yet/);
  assert.equal(countTotal([{ key: "migrated", count: 580 }, { key: "opening", count: 13 }]), 593);
});

test("phase cards format every state as a readable badge", () => {
  const fixtures: [string | null, string, string][] = [
    [null, "Not run", "neutral"], ["queued", "Waiting", "neutral"], ["running", "Running", "info"],
    ["completed", "Done", "ok"], ["failed", "Failed", "bad"],
  ];
  for (const [status, label, tone] of fixtures) { assert.equal(phaseStatusLabel(status), label); assert.equal(phaseStatusTone(status), tone); }
});

const loadFixture: MigrationApply = {
  loadId: "load-internal", batchId: "batch-internal", mapRunId: "map-internal", rehearsal: true, status: "completed",
  requestedByName: "R. Okafor", requestedAt: "2026-09-01T08:00:00Z", startedAt: "2026-09-01T08:00:01Z", finishedAt: "2026-09-01T08:02:00Z", failure: null,
  unitsInPlan: 593, applied: 580, alreadyApplied: 0, refused: 13, eventsWritten: 2267, entitlementsWritten: 341,
  replacementLinksWritten: 5, certificatesRecorded: 580, rolledBackBy: null, rolledBackAt: null, gapReportRunId: "gap-internal", coverGaps: 17,
  gapsByKind: [{ kind: "card_cover_policy_not_set", label: "Equipment-card cover policy not set", count: 9 }],
};

test("load card maps fixture counts, watches active loads and filters the cover-gap link", () => {
  for (const field of ["applied", "refused", "eventsWritten", "entitlementsWritten", "replacementLinksWritten", "certificatesRecorded", "coverGaps"]) assert.match(consoleScreen, new RegExp(`load\\.${field}`));
  assert.deepEqual([loadFixture.applied, loadFixture.refused, loadFixture.eventsWritten, loadFixture.entitlementsWritten, loadFixture.replacementLinksWritten, loadFixture.certificatesRecorded, loadFixture.coverGaps], [580, 13, 2267, 341, 5, 580, 17]);
  assert.match(consoleScreen, /setInterval\(\(\) => void loadPhases\(\), 3000\)/);
  assert.match(consoleScreen, /\/entitlements\/gaps\?channels=migration/);
  assert.match(gapReportScreen, /searchParams\.get\("channels"\)/);
  for (const status of ["queued", "running", "completed", "failed", "rolled_back"]) { assert.doesNotMatch(loadStatusLabel(status), /_/); assert.ok(loadStatusTone(status)); }
});

test("final load gives a distinct disabled reason for each incomplete gate", () => {
  const ready = { ...validationRun, rehearsal: false, status: "completed", hardFailed: 0, signedChecks: validationRun.checksTotal };
  assert.equal(finalLoadDisabledReason(null), "Run final validation first");
  assert.equal(finalLoadDisabledReason({ ...ready, rehearsal: true }), "The latest validation run is a rehearsal; run final validation");
  assert.equal(finalLoadDisabledReason(ready), "Create the final sign-off record for this load plan");
  assert.equal(finalLoadDisabledReason({ ...ready, hasFinalSignoff: true }), null);
  assert.match(consoleScreen, /finalDisabledReason/);
  assert.match(consoleScreen, /className=\{`btn sm\$\{reason \? "" : enabledClass\}`\}/);
  assert.match(consoleScreen, /migration-disabled-reason/);
  assert.match(consoleScreen, /<ErrorNote error=\{error\}/);
});

test("rehearsal rollback uses an inline confirmation and keeps touched loads in place", () => {
  for (const text of ["Roll back this rehearsal?", "Every unit, fact, cover row, link and certificate this load wrote will be removed", "Keep the load", "The rehearsal load stays in place"]) assert.ok(consoleScreen.includes(text), text);
  assert.doesNotMatch(consoleScreen, /window\.confirm|confirm\(/);
  assert.match(consoleScreen, /result\.touchedUnitCount/);
  assert.match(consoleScreen, /result\.touchedUnits/);
  assert.match(consoleScreen, /migrationApi\.touchedLoadUnits/);
  assert.match(consoleScreen, /Touched serial/);
  for (const outcome of ["rolled_back", "refused_touched", "nothing_to_remove"]) assert.doesNotMatch(rollbackOutcomeLabel(outcome), /_/);
});

test("load actions are full width and administrator-only while refusals use their own scalable view", () => {
  assert.match(consoleScreen, /<LoadActions .*admin=\{admin\}/);
  assert.match(consoleScreen, /if \(!admin\) return/);
  assert.match(consoleScreen, /migration-load-action-row/);
  assert.match(consoleScreen, /integration\/migration\/loads\/\$\{load\.loadId\}\/refusals/);
  for (const text of ["Total refused", "Reason", "Item code", "Serial contains", "Why it was refused", "Export filtered CSV", "keyset paged", "reasons shown in full"]) assert.ok(refusalsScreen.includes(text), text);
  assert.match(refusalsScreen, /<KpiStrip/); assert.match(refusalsScreen, /<DataGrid/);
  assert.match(refusalsScreen, /migrationApi\.loadRefusals\(loadId, \{ \.\.\.active, cursor \}\)/);
  assert.match(refusalsScreen, /result\?\.total/); assert.match(refusalsScreen, /totalIsExact: true/);
  assert.match(refusalsScreen, /loadRefusalsExportUrl/);
  assert.doesNotMatch(refusalsScreen, />\{row\.(?:refusalId|planUnitId)\}</);
});

test("every emitted load refusal and map gap has readable formatting", () => {
  const refusals = [
    "item_not_synced", "item_unknown", "item_not_serial_tracked", "already_open_on_another_unit", "duplicate_in_batch", "replacement_unit_not_loaded",
    "entitlement_policy_unknown", "entitlement_policy_not_published", "entitlement_policy_version_differs", "entitlement_policy_family_differs",
    "entitlement_direction_unknown", "entitlement_start_required", "entitlement_dates_invalid", "entitlement_termination_before_start",
    "entitlement_evidence_required", "entitlement_selected_evidence_required", "entitlement_selected_evidence_differs", "family_occupied",
    "migration_certificate_version_not_one", "migration_certificate_provenance_invalid", "migration_certificate_unknowns_required",
    "migration_certificate_evidence_required", "migration_certificate_required", "replacement_link_invalid", "replacement_policy_must_be_unknown",
    "identifier_required", "history_depth_unknown", "condition_grade_unknown", "ownership_party_is_us", "identifier_type_repeated",
    "internal_serial:character_not_encodable_in_code128:*",
  ];
  for (const reason of refusals) assert.doesNotMatch(refusalReasonLabel(reason), /_/);
  assert.match(consoleScreen, /\{row\.label\}/);
  assert.equal(refusalReasonLabel("duplicate_in_batch"), "Identifier is duplicated in this load");
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
    { sourceKey: "candidate-a", serialAsStored: "RS-101", itemCode: "RS-1100", itemName: "Rack server", transactions: 12, firstReceipt: "2026-09-01", lastDelivery: "2026-09-03", lastDeliveryParty: "Asterra Compute Works", hasEquipmentCard: true, cardNumber: "48810", hasContract: true, contractNumber: "CT-2025-0911", receiptDocumentNumber: 70110, receiptDocumentLine: 0, disposition: "review" },
    { sourceKey: "candidate-b", serialAsStored: " rs-101 ", itemCode: "RS-1100", itemName: "Rack server", transactions: 1, firstReceipt: "2026-09-01", lastDelivery: null, lastDeliveryParty: null, hasEquipmentCard: false, cardNumber: null, hasContract: false, contractNumber: null, receiptDocumentNumber: 70110, receiptDocumentLine: 0, disposition: "review" },
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
  const history = reviewDecisionOptions({ ...reviewDetail, kind: "history_needs_review", suggestion: "accept_as_recorded" });
  assert.deepEqual(history.map(option => option.label), ["Accept as recorded", "Exclude from history", "Fix in the incumbent"]);
  assert.equal(history[0]!.suggested, true);
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
  for (const decision of ["merge_into", "keep_apart", "fix_in_incumbent", "accept_as_recorded", "exclude_from_history"] as const) assert.doesNotMatch(reviewDecisionLabel(decision), /_/);
  for (const rule of ["exact", "trimmed_upper", "family_normalised", "reviewed", "no_serial_match"]) assert.doesNotMatch(matchRuleLabel(rule), /_/);
  for (const status of ["queued", "running", "completed", "failed", "not_run"]) assert.doesNotMatch(canonicalisationStatusLabel(status), /_/);
  assert.equal(serialAsStored(" RS-101 "), '" RS-101 "');
  assert.equal(serialAsStored("RS-101"), "RS-101");
  assert.equal(serialAsStored("RS\t101"), '"RS\\t101"');
  assert.equal(serialAsStored(null), "No serial recorded");
  assert.equal(candidateLetter(0), "A"); assert.equal(candidateLetter(25), "Z"); assert.equal(candidateLetter(26), "AA");
  assert.doesNotMatch(reviewScreen, />\{candidate\.sourceKey\}</);
});
