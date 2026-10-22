import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import type { ParallelRunDashboard } from "../src/integration/adminTypes.ts";
import { parallelRunApi } from "../src/integration/parallelRunApi.ts";
import { goLiveStatus, handoverState, lag, parallelEyebrow, parallelKpis } from "../src/integration/parallelRunModel.ts";

const screen = readFileSync(new URL("../src/integration/MigrationParallelRun.tsx", import.meta.url), "utf8");
const routes = readFileSync(new URL("../src/routes.tsx", import.meta.url), "utf8");
const common = readFileSync(new URL("../src/integration/MigrationCommon.tsx", import.meta.url), "utf8");
const apiSource = readFileSync(new URL("../src/integration/parallelRunApi.ts", import.meta.url), "utf8");

const dashboard: ParallelRunDashboard = {
  connectionId: "connection-internal", connectionLabel: "Primary source", status: "in_progress", days: 30,
  startDate: "2026-09-01", endDate: "2026-09-30", dayNumber: 10, daysLeft: 20, monthEndCloseOn: "2026-09-30",
  cutoverAt: "2026-09-01T07:30:00Z", syncLag: { nowSeconds: 42, targetSeconds: 180, shareWithinTargetToday: 95, messagesToday: 80 },
  conflicts: { open: 7, oldestDetectedAt: "2026-09-08T08:00:00Z", autoResolved: 0 },
  failedMessages: { open: 1, olderThanSevenDays: 0 },
  reconciliation: { runs: 12, clean: 10, explained: 1, mismatch: 1, failed: 0, lastExplainedOn: "2026-09-09" },
  handover: [
    { order: 1, kind: "installed_base_and_cover", label: "Installed base and cover", description: "The module holds owner and cover.", state: "module_owns", plannedOn: "2026-09-01", ownedSince: "2026-09-01", evidence: "Permissions checked", confirmedByName: "Anika Iyer", confirmedAt: "2026-09-01T08:00:00Z", facts: ["Contracts are maintained in the module"], canConfirm: false, confirmRefusals: [] },
    { order: 2, kind: "returns", label: "Returns", description: "Return authorisations move next.", state: "next", plannedOn: "2026-09-20", ownedSince: null, evidence: null, confirmedByName: null, confirmedAt: null, facts: ["Three open returns"], canConfirm: false, confirmRefusals: [{ code: "handover_previous_kind_open", message: "The previous handover is open." }] },
  ],
  goLive: { done: 1, total: 7, issuedAt: null, recordId: null, canIssue: false, issueRefusals: [{ code: "go_live_items_open", message: "Six items are still open." }], items: [
    { code: "validation_signed_off", label: "Validation signed off", source: "measured", status: "done", detail: "R4 · 14 of 14 checks", evidence: null, confirmedByName: null, confirmedAt: null },
    { code: "users_trained", label: "Users trained", source: "confirmed", status: "open", detail: "No evidence yet", evidence: null, confirmedByName: null, confirmedAt: null },
  ] },
};

test("parallel-run route and tab occupy the approved location", () => {
  assert.match(routes, /integration\/migration\/parallel-run.*MigrationParallelRun/);
  const validation = common.indexOf('"Validation and sign-off"'); const parallel = common.indexOf('"Parallel run"'); const settings = common.indexOf('"Settings"');
  assert.ok(validation >= 0 && parallel > validation && settings > parallel);
  assert.match(screen, /params\.get\("connection"\)/);
  assert.match(screen, /Connection health/);
  assert.match(screen, /Roll back…/);
});

test("dashboard formatting maps every KPI and the server-labelled handover state", () => {
  const kpis = parallelKpis(dashboard, Date.parse("2026-09-10T08:00:00Z"));
  assert.deepEqual(kpis.map(item => item.label), ["Days left", "Sync lag now", "Conflicts to review", "Failed messages", "Nightly reconciliation"]);
  assert.equal(kpis[1]!.value, "42 s"); assert.match(kpis[1]!.detail!, /95% within 3 min today/);
  assert.match(kpis[2]!.detail!, /oldest 2 days · none auto-resolved/);
  assert.equal(kpis[4]!.value, "11 of 12"); assert.match(kpis[4]!.detail!, /1 explained/);
  assert.match(parallelEyebrow(dashboard), /day 10 of 30 · both systems live, the module owns installed base and cover/);
  assert.equal(handoverState(dashboard.handover[1]!), `Next · ${new Intl.DateTimeFormat(undefined, { day: "numeric", month: "short", timeZone: "UTC" }).format(new Date("2026-09-20T00:00:00Z"))}`);
  assert.equal(lag(180), "3 min"); assert.equal(goLiveStatus(dashboard.goLive.items[1]!), "Open");
  assert.equal(parallelKpis({ ...dashboard, syncLag: { ...dashboard.syncLag, messagesToday: 0 } })[1]!.detail, "no messages today");
});

test("every approved card, refusal and not-started state is present", () => {
  for (const text of ["Start parallel run", "Handover by kind of record", "Go-live record", "Conflicts waiting", "Nightly reconciliation", "Review reconciliation runs…"]) assert.ok(screen.includes(text), text);
  assert.match(screen, /dashboard\?\.status === "not_started"/);
  assert.match(screen, /<KpiStrip/);
  assert.match(screen, /<DataGrid/);
  assert.match(screen, /step\.confirmRefusals/);
  assert.match(screen, /dashboard\.goLive\.issueRefusals/);
  assert.match(screen, /reason\.message/);
  assert.doesNotMatch(screen, />\{(?:conflict|dashboard|step|item)\.(?:connectionId|conflictId|recordId|manualTaskId|decidedBy)\}</);
});

test("all write drawers enforce evidence limits and submit through typed client calls", () => {
  for (const name of ["openPlanHandover", "openConfirmHandover", "openConfirmGoLive", "openConflict", "openReconciliation"]) assert.match(screen, new RegExp(`function ${name}`));
  assert.match(screen, /maxLength=\{500\}/g);
  assert.match(screen, /parallelRunApi\.setMonthEndClose/);
  assert.match(screen, /parallelRunApi\.planHandover/);
  assert.match(screen, /parallelRunApi\.confirmHandover/);
  assert.match(screen, /parallelRunApi\.confirmGoLiveItem/);
  assert.match(screen, /parallelRunApi\.resolveConflict/);
  assert.match(screen, /parallelRunApi\.explainReconciliation/);
  assert.match(screen, /<ErrorNote error=\{error\}/g);
  assert.match(screen, /migration_owner_differs|server refusal message|ErrorNote/);
});

test("typed parallel-run client sends the exact contracted paths, verbs and bodies", async () => {
  const original = globalThis.fetch; const calls: { path: string; method: string; body: unknown }[] = [];
  globalThis.fetch = async (input, init) => {
    calls.push({ path: String(input), method: init?.method ?? "GET", body: init?.body ? JSON.parse(String(init.body)) : null });
    return new Response(JSON.stringify(dashboard), { status: 200, headers: { "content-type": "application/json" } });
  };
  try {
    await parallelRunApi.start("connection-internal", 30, "2026-09-01");
    await parallelRunApi.setMonthEndClose("connection-internal", "2026-09-30");
    await parallelRunApi.planHandover("connection-internal", "returns", "2026-09-20");
    await parallelRunApi.confirmHandover("connection-internal", "returns", "Permissions checked");
    await parallelRunApi.confirmGoLiveItem("connection-internal", "users_trained", "14 of 14 trained");
    await parallelRunApi.issueGoLiveRecord("connection-internal");
    await parallelRunApi.resolveConflict("conflict-internal", "accept_remote", "Customer confirmed the transfer");
    await parallelRunApi.explainReconciliation("run-internal", "Late source message arrived next day");
  } finally { globalThis.fetch = original; }
  assert.deepEqual(calls, [
    { path: "/api/integration/connections/connection-internal/parallel-run", method: "POST", body: { days: 30, startDate: "2026-09-01" } },
    { path: "/api/integration/connections/connection-internal/parallel-run/month-end-close", method: "PUT", body: { closeOn: "2026-09-30" } },
    { path: "/api/integration/connections/connection-internal/parallel-run/handover/returns/plan", method: "PUT", body: { plannedOn: "2026-09-20" } },
    { path: "/api/integration/connections/connection-internal/parallel-run/handover/returns/confirm", method: "POST", body: { evidence: "Permissions checked" } },
    { path: "/api/integration/connections/connection-internal/go-live/users_trained/confirm", method: "POST", body: { evidence: "14 of 14 trained" } },
    { path: "/api/integration/connections/connection-internal/go-live/record", method: "POST", body: null },
    { path: "/api/integration/coexistence/conflicts/conflict-internal/resolve", method: "POST", body: { decision: "accept_remote", note: "Customer confirmed the transfer" } },
    { path: "/api/integration/reconciliation/runs/run-internal/explain", method: "POST", body: { note: "Late source message arrived next day" } },
  ]);
  assert.doesNotMatch(apiSource, /actor|requestedBy|confirmedBy/i);
});

test("dashboard, conflict and issued-record reads use contracted filters and download URL", async () => {
  const original = globalThis.fetch; const calls: string[] = [];
  globalThis.fetch = async input => { calls.push(String(input)); return new Response(JSON.stringify(dashboard), { status: 200, headers: { "content-type": "application/json" } }); };
  try {
    await parallelRunApi.dashboard("connection-internal");
    await parallelRunApi.conflicts("connection-internal");
    await parallelRunApi.goLiveRecord("record-internal");
  } finally { globalThis.fetch = original; }
  assert.deepEqual(calls, [
    "/api/integration/connections/connection-internal/parallel-run/dashboard",
    "/api/integration/coexistence/conflicts?connectionId=connection-internal&status=unresolved&take=5",
    "/api/integration/go-live/records/record-internal",
  ]);
  assert.equal(parallelRunApi.goLiveCsvUrl("record-internal"), "/api/integration/go-live/records/record-internal/csv");
});

test("a conflict decision refusal keeps the server's migration-owner message", async () => {
  const original = globalThis.fetch;
  globalThis.fetch = async () => new Response(JSON.stringify({ title: "migration_owner_differs", detail: "Accept the source owner or keep the module owner by dismissing this finding.", rule: "migration_owner_differs" }), { status: 422, headers: { "content-type": "application/problem+json" } });
  try {
    await assert.rejects(() => parallelRunApi.resolveConflict("conflict-internal", "keep_module", "The module owner is correct"), error => {
      assert.equal((error as { rule?: string }).rule, "migration_owner_differs");
      assert.match((error as Error).message, /Accept the source owner/);
      return true;
    });
  } finally { globalThis.fetch = original; }
});
