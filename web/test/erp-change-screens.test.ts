import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import type { ConnectionAuthorisationView, ErpChangeMyWorkView, ErpChangeRunbookView, MasterDataExceptionRow, SignInHistoryItem, StockDifferenceRow } from "../src/integration/erpChangeTypes.ts";
import {
  canCancel, canReverse, checklistLabel, daysUntil, erpChangeRights, exceptionDecision, exitCriteriaStates, historyBy, historyEvent, historyResult,
  erpChangeWorkSummary, mayDecideDivergence, notificationRecipientRoles, parseReminderDays, problemLabel, runbookKpis, signInNote, signInStatusLabel, stepSummaries, stockReasonLabel, stockReasonTone, suggestionLabel,
} from "../src/integration/erpChangeModel.ts";

const read = (path: string) => readFileSync(new URL(`../src/${path}`, import.meta.url), "utf8");
const routes = read("routes.tsx");
const runbook = read("integration/ErpChangeRunbook.tsx");
const master = read("integration/ErpChangeMasterData.tsx");
const stock = read("integration/ErpChangeStock.tsx");
const common = read("integration/ErpChangeCommon.tsx");
const integrationTabs = read("integration/Common.tsx");
const api = read("integration/erpChangeApi.ts");
const signIn = read("integration/ConnectionSignIn.tsx");
const callback = read("integration/ConnectionSignInCallback.tsx");
const connections = read("integration/Connections.tsx");
const overview = read("pages/Overview.tsx");
const screens = [runbook, master, stock, common, signIn, callback];

const run: ErpChangeRunbookView = {
  runId: "run-internal", status: "active", currentStep: 3,
  source: { connectionId: "a", name: "Head office ERP", connectorCode: "CX01", status: "active", health: "healthy", readOnly: false, outboundMode: "documents" },
  target: { connectionId: "b", name: "New ERP", connectorCode: "CX02", status: "active", health: "healthy", readOnly: true, outboundMode: "none" },
  startedBy: "Priya Raman", startedAt: "2026-09-03T09:40:00Z", closedBy: null, closedAt: null, closeReason: null,
  checks: [
    { step: 1, passed: true, counts: { healthy: 1 }, detail: "Smoke read 250 items, 120 parties", checkedAt: "2026-09-03T09:40:00Z", checkedBy: "Priya Raman" },
    { step: 2, passed: true, counts: { open_exceptions: 0 }, detail: "37 exceptions, all decided", checkedAt: "2026-09-06T16:05:00Z", checkedBy: "Priya Raman" },
  ],
  transitions: [], unitsCompared: 8412, openDifferences: 212, coveredOpenDifferences: 41, resolvedDifferences: 1186, openMasterDataExceptions: 0,
  dualRun: null, cutoverChecklist: null, archive: null, unitHistoriesChanged: 0,
  deliberateUnitHistoriesChanged: 2, unitHistoryCheckedAt: "2026-09-03T09:40:00Z",
};

test("ERP change screens sit under Integration with their tab bar and the sign-in pages", () => {
  for (const path of ["integration/erp-change\"", "integration/erp-change/master-data", "integration/erp-change/stock", "integration/connections/:connectionId/sign-in\"", "integration/connections/:connectionId/sign-in/callback"])
    assert.ok(routes.includes(path), path);
  for (const label of ["Runbook", "Master data exceptions", "Stock differences", "Dual-run divergences", "Cutover checklist"]) assert.match(common, new RegExp(`"${label}"`));
  assert.match(runbook, /id="dual"/); assert.match(runbook, /id="cutover"/);
  assert.match(integrationTabs, /"ERP change"/);
  assert.match(integrationTabs, /useErpChangeInProgress/);
  assert.match(connections, /features\??\.interactiveSignIn[^\n]*sign-in/);
});

test("each action calls its exact ERP change endpoint", () => {
  for (const path of ["/check", "/advance", "/cancel", "/reverse", "/master-data/remap", "/choose-candidate", "/map`", "/not-needed", "/stock/reconcile", "/stock/sign-off", "/resolve`", "/dual-run/start", "/dual-run/check", "-divergences/${divergenceId}/accept-source", "-divergences/${divergenceId}/explain", "/cutover/start", "/cutover/validations", "/cutover/drain", "/cutover/enable-target", "/cutover/confirm-first-outbound", "/cutover/reconcile", "/archive", "/settings/clean-period", "/cx04`", "/authorisation", "/consent", "/callback", "/disable", "/reminder-settings"])
    assert.ok(api.includes(path), path);
  assert.match(api, /body: \{ reason, confirm: true \}/);
});

test("home notice is role-gated, hidden for zero work and links to the server-selected screen", () => {
  const work: ErpChangeMyWorkView = { runId: "internal", currentStep: 3, sourceName: "Northstar Office Systems", targetName: "Silverline Systems", masterDataExceptions: 0, coveredStockDifferences: 41, otherStockDifferencesAwaitingSignOff: 171, stockTypeDivergences: 0, screenPath: "/integration/erp-change/stock", total: 212 };
  assert.equal(erpChangeWorkSummary(work), "41 covered-unit differences block it; 171 others need your sign-off");
  assert.match(overview, /item_master_owner.*inventory_controller/);
  assert.match(overview, /!work \|\| work\.total === 0/);
  assert.match(overview, /to=\{work\.screenPath\}/);
  assert.match(overview, /ERP change in progress — step \{work\.currentStep\}/);
});

test("notification settings are visible only to ERP change controllers", () => {
  assert.match(runbook, /rights\.control && <NotificationSettings/);
  assert.match(runbook, /Morning digest/);
  assert.match(runbook, /Tenant-local hour \(0–23\)/);
  assert.match(api, /settings\/notifications/);
  assert.deepEqual(notificationRecipientRoles.map(([role]) => role), ["item_master_owner", "inventory_controller"]);
});

test("rights mirror the server policies, and stock is the inventory controller's alone", () => {
  assert.deepEqual(erpChangeRights(["tenant_administrator"]), { read: true, control: true, masterData: false, documentDivergences: false, stock: false });
  assert.deepEqual(erpChangeRights(["inventory_controller"]), { read: true, control: false, masterData: false, documentDivergences: false, stock: true });
  assert.deepEqual(erpChangeRights(["item_master_owner"]), { read: true, control: false, masterData: true, documentDivergences: false, stock: false });
  assert.equal(erpChangeRights(["technician"]).read, false);
  assert.equal(mayDecideDivergence({ kind: "stock" }, erpChangeRights(["integration_operator"])), false);
  assert.equal(mayDecideDivergence({ kind: "document" }, erpChangeRights(["integration_operator"])), true);
  assert.match(stock, /rights\.stock && onStep/);
  assert.match(stock, /I am the inventory controller/);
});

test("runbook steps, exit criteria and figures follow the approved runbook", () => {
  const steps = stepSummaries(run);
  assert.deepEqual(steps.map(step => step.value), ["Done", "Done", "212", "—", "—", "—"]);
  assert.equal(steps[2]!.current, true); assert.equal(steps[2]!.tone, "bad");
  assert.match(steps[2]!.detail, /41 with live cover/);
  assert.equal(steps[3]!.detail, "30 days minimum");
  assert.deepEqual(exitCriteriaStates(run).map(item => item.state), ["passed", "passed", "open", "not_started", "not_started", "not_started"]);
  assert.match(exitCriteriaStates(run)[0]!.detail, /checked by Priya Raman/);
  assert.deepEqual(runbookKpis(run).map(kpi => kpi.value), ["8,412", "212", "1,186", "0", "0"]);
});

test("run history, parked messages and changed unit history use paged server reads", () => {
  for (const path of ["/runs${query", "/unit-history-changes", "/cutover/parked${query", "/cutover/parked-to-manual"])
    assert.ok(api.includes(path), path);
  assert.match(runbook, /Past changes/);
  assert.match(runbook, /select a row to open it read-only/);
  assert.match(runbook, /Parked messages/);
  assert.match(runbook, /Convert to manual task/);
  assert.match(runbook, /Review affected units/);
  assert.match(runbook, /units changed by signed-off resolutions/);
  assert.match(runbook, /History change kind/);
  assert.match(api, /query\(\{ kind, cursor, take: 12 \}\)/);
  assert.match(runbook, /rights\.control/);
});

test("roll back is a pause before cutover and only a new change back after it, never a one-click undo", () => {
  assert.equal(canCancel(run), true); assert.equal(canReverse(run), false);
  const afterCutover = { ...run, currentStep: 5 };
  assert.equal(canCancel(afterCutover), false); assert.equal(canReverse(afterCutover), true);
  assert.match(runbook, /Roll back to the current ERP…/);
  assert.match(runbook, /Start an ERP change back…/);
  assert.match(runbook, /no one-click undo/);
  assert.match(runbook, /disabled=\{action\.busy \|\| !reason\.trim\(\) \|\| !confirmed\}/);
});

test("cutover checklist items read as the approved order and outbound kinds", () => {
  assert.equal(checklistLabel({ code: "freeze_source" })[0], "Stop outbound on the current ERP");
  assert.equal(checklistLabel({ code: "validate:CreateCreditMemo" })[0], "Check credit note on the new ERP");
  assert.equal(checklistLabel({ code: "first_outbound:CreateReplacementDelivery" })[0], "First replacement delivery confirmed");
  assert.match(runbook, /pendingActionsCsvUrl/);
  assert.match(runbook, /Clean days in a row \(tenant setting, 1–90\)/);
});

test("master data exceptions show the problem, decision and no automatic merge", () => {
  const row: MasterDataExceptionRow = { exceptionId: "x", kind: "item", targetExternalId: "ITM-ST44-B", displayName: "ST-44 storage array, 24-bay", taxId: null, country: null, problem: "ambiguous", candidates: [{ moduleId: "m1", label: "ST-44" }, { moduleId: "m2", label: "ST-44-HD" }], liveUnitsAffected: 412, status: "mapped", resolvedModuleId: "m1", resolvedBy: "Priya Raman", resolvedAt: "2026-09-05T10:00:00Z", resolutionNote: null };
  assert.equal(problemLabel(row), "Was ambiguous");
  assert.equal(problemLabel({ ...row, status: "open", problem: "unmatched" }), "No match");
  assert.equal(exceptionDecision(row), "Mapped to ST-44 · Priya Raman");
  assert.match(master, /never merged automatically/);
  assert.match(master, /<ItemField/); assert.match(master, /<PartyField/);
});

test("stock differences label reasons and suggestions, and resolve only through unit commands", () => {
  const row = { reason: "location_differs", hasLiveEntitlement: true, status: "open" } as StockDifferenceRow;
  assert.equal(stockReasonLabel("missing_in_b"), "Missing in new ERP");
  assert.equal(stockReasonTone(row), "warn");
  assert.equal(stockReasonTone({ ...row, reason: "serial_differs_by_normalisation" }), "info");
  assert.equal(suggestionLabel("none_same_after_normalising"), "None · same unit after normalising");
  assert.match(stock, /reasonCounts\.find/);
  assert.match(stock, /count\?\.open/);
  for (const command of ["change_custody", "correct_identifier", "merge_units"]) assert.match(stock, new RegExp(`"${command}"`));
  assert.match(stock, /commandId: crypto\.randomUUID\(\)/);
});

const signInView: ConnectionAuthorisationView = {
  status: "active", refreshChainExpiresAt: "2026-09-19T09:14:00Z", refreshExpiryMode: "absolute", refreshLifetimeDays: 90, lastCallAt: "2026-09-07T06:58:00Z",
  reminderDays: [14, 3], reminderRecipientRoles: ["integration_operator"], remindersSent: [14], nextReminderDays: 3,
  signedInBy: "Rohan Iyer", signedInAt: "2026-06-21T09:14:00Z", disabledBy: null, disabledAt: null, disabledReason: null, waitingOutbound: 0,
  licenceTier: "standard", licensedRequestsPerMinute: 100, governedRequestsPerMinute: 80, requestHeadroomPerMinute: 20, canDisable: true, requiresFreshConsent: false,
  history: { items: [], nextCursor: null },
};

test("sign-in status, expiry warning and history read as the approved screens", () => {
  const now = Date.parse("2026-09-07T09:14:00Z");
  assert.equal(daysUntil(signInView.refreshChainExpiresAt, now), 12);
  assert.deepEqual(signInNote(signInView, now), { tone: "warn", title: "Sign-in expires in 12 days" });
  assert.equal(signInNote(signInView, Date.parse("2026-08-01T00:00:00Z")), null);
  assert.deepEqual(signInNote({ ...signInView, status: "disabled" }, now), { tone: "bad", title: "Disabled" });
  assert.equal(signInStatusLabel({ status: "reconsent_required" }), "Sign in again needed");
  const renewal: SignInHistoryItem = { sequence: 1, kind: "access_renewed", by: "System", occurredAt: "2026-09-07T00:00:00Z", result: "ok", count: 24 };
  assert.equal(historyEvent(renewal), "Access renewed (24 times)"); assert.equal(historyBy(renewal), "module");
  assert.deepEqual(historyResult({ ...renewal, kind: "disabled", result: "tokens_deleted" }), ["Tokens deleted", "bad"]);
  assert.equal(historyEvent({ ...renewal, kind: "expiry_reminder", daysBeforeExpiry: 14 }), "Expiry reminder, 14 days");
  assert.equal(historyEvent({ ...renewal, kind: "sync_paused" }), "Tenant administrator told sync is paused");
  assert.match(signIn, /gridId: "integration-sign-in-history"/);
  assert.match(signIn, /label: "When"[^\n]+sort: \{ key: "when"/);
  assert.match(api, /authorisation\$\{query\(\{ cursor, sort: grid\.sort\.key, dir: grid\.sort\.dir, take: grid\.pageSize \}\)\}/);
});

test("reminder days are one to three distinct values from 1 to 90", () => {
  assert.deepEqual(parseReminderDays("3, 14"), [14, 3]);
  assert.equal(parseReminderDays(""), null);
  assert.equal(parseReminderDays("14 14"), null);
  assert.equal(parseReminderDays("1 2 3 4"), null);
  assert.equal(parseReminderDays("0"), null);
  assert.equal(parseReminderDays("91"), null);
});

test("Disable needs a reason and a confirmation; the password is never asked for", () => {
  assert.match(signIn, /className="btn danger"[^\n]*aria-label="Disable this connection's sign-in"/);
  assert.match(signIn, /disabled=\{action\.busy \|\| !reason\.trim\(\) \|\| !confirmed\}/);
  assert.doesNotMatch(signIn, /type="password"/);
  assert.match(signIn, /window\.location\.assign\(start\.consentUrl\)/);
  assert.match(callback, /window\.history\.replaceState/);
  assert.doesNotMatch(callback, /console\./);
});

test("icon buttons carry labels and tooltips, and screens show no raw ids", () => {
  for (const match of signIn.matchAll(/<button type="button"[^>]*><Icon/g)) { assert.match(match[0], /aria-label=/); assert.match(match[0], /title=/); }
  for (const screen of screens) {
    // Rendered as text (a JSX child), never as a prop value such as key= or value=.
    assert.doesNotMatch(screen, /[>}]\s*\{(?:row|run|item|view|run\.source|run\.target)\.(?:runId|divergenceId|exceptionId|differenceId|unitId|subjectId|moduleId|connectionId)\}\s*</);
  }
});
