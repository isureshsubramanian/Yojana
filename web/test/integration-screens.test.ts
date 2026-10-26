import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import { nextGridSort } from "../src/components/gridPreferences.ts";
import { adminApi as adminClient } from "../src/integration/adminApi.ts";
import { approvalNeedsAcceptance, age, localDate, draftValueIsMono, mayOperateIntegration, maySeeIntegrationIndicator, reasonLabel, reconciliationRange } from "../src/integration/model.ts";

const messages = readFileSync(new URL("../src/integration/MessagesAttention.tsx", import.meta.url), "utf8");
const manual = readFileSync(new URL("../src/integration/ManualTasks.tsx", import.meta.url), "utf8");
const api = readFileSync(new URL("../src/integration/api.ts", import.meta.url), "utf8");

test("both integration screens render the approved headings and shared server-paged grid", () => {
  assert.match(messages, /title="Messages needing attention"/);
  assert.match(manual, /title="ERP entries to make by hand"/);
  for (const screen of [messages, manual]) {
    assert.match(screen, /<IntegrationTabs/);
    assert.match(screen, /<DataGrid/);
    assert.match(screen, /nextCursor/);
    assert.match(screen, /onPage=/);
  }
  assert.equal(reasonLabel("item_not_mapped"), "Unknown item");
});

test("attention and manual-task grids declare sortable headers and reset paging through the shared query", () => {
  assert.match(messages, /useGridQuery\("\/api\/integration\/dead-letters"/);
  assert.match(messages, /sort: \{ key: "received"/);
  assert.match(messages, /onSort=\{query\.onSort\}/);
  assert.match(messages, /onPageSize=\{query\.onPageSize\}/);
  assert.match(manual, /useGridQuery\("\/api\/integration\/manual-tasks"/);
  for (const key of ["created", "desk"]) assert.match(manual, new RegExp(`sort: \\{ key: "${key}"`));
  assert.match(manual, /onSort=\{query\.onSort\}/);
  assert.match(manual, /onPageSize=\{query\.onPageSize\}/);
  assert.match(adminCommon, /useGridState/);
  assert.match(adminCommon, /gridState\.onPageSize/);
});

test("message and hand-entry actions call their exact integration endpoints", () => {
  assert.match(api, /dead-letters\/\$\{id\}\/map-and-retry/);
  assert.match(api, /dead-letters\/\$\{id\}\/create-provisional-and-retry/);
  assert.match(api, /dead-letters\/\$\{id\}\/discard/);
  assert.match(api, /manual-tasks\/\$\{id\}\/complete/);
  assert.match(api, /body: \{ reason \}/);
  assert.match(api, /allowUnverified/);
  assert.match(messages, /integrationApi\.mapAndRetry/);
  assert.match(messages, /integrationApi\.createProvisionalAndRetry/);
  assert.match(manual, /integrationApi\.completeManualTask/);
});

test("discard and cancel controls require a reason before they can run", () => {
  assert.match(messages, /disabled=\{busy \|\| !discardReason\.trim\(\)\}/);
  assert.match(messages, /placeholder="Reason \(required\)" required/);
  assert.match(manual, /disabled=\{busy \|\| !cancelReason\.trim\(\)\}/);
  assert.match(manual, /Record anyway/);
});

test("integration indicator and operations use their approved role gates", () => {
  assert.equal(maySeeIntegrationIndicator(["tenant_administrator"]), true);
  assert.equal(maySeeIntegrationIndicator(["service_manager"]), true);
  assert.equal(maySeeIntegrationIndicator(["integration_operator"]), true);
  assert.equal(maySeeIntegrationIndicator(["technician"]), false);
  assert.equal(mayOperateIntegration(["service_manager"]), false);
  assert.equal(mayOperateIntegration(["integration_operator"]), true);
});

test("operator-facing screen copy does not expose internal transport terms", () => {
  for (const screen of [messages, manual]) assert.doesNotMatch(screen, /["'`](?:[^"'`]*\b(?:DLQ|outbox|canonical|idempotency)\b[^"'`]*)["'`]/i);
});

test("the serial face is limited to copy-exact identifiers", () => {
  for (const key of ["serial", "serialNumber", "itemCode", "ERP key", "document number", "reference (paste exactly)"])
    assert.equal(draftValueIsMono(key), true, key);
  for (const key of ["posting date", "customer code", "customer", "remarks", "description", "document type", "unit price"])
    assert.equal(draftValueIsMono(key), false, key);
  assert.doesNotMatch(messages, /key: "received"[^\n]*mono: true/);
  assert.match(manual, /className="input mono"/);
  assert.match(manual, /placeholder="for example 30552"/);
});

test("every message state chip renders its server-side count", () => {
  assert.match(messages, /result\.summary\.retried/);
  assert.match(messages, /result\.summary\.discarded/);
  assert.match(messages, /count\.toLocaleString\(\)/);
});

const adminApi = readFileSync(new URL("../src/integration/adminApi.ts", import.meta.url), "utf8");
const adminCommon = readFileSync(new URL("../src/integration/AdminCommon.tsx", import.meta.url), "utf8");
const connections = readFileSync(new URL("../src/integration/Connections.tsx", import.meta.url), "utf8");
const settings = readFileSync(new URL("../src/integration/ConnectionSettings.tsx", import.meta.url), "utf8");
const health = readFileSync(new URL("../src/integration/ConnectionHealth.tsx", import.meta.url), "utf8");
const reconciliation = readFileSync(new URL("../src/integration/Reconciliation.tsx", import.meta.url), "utf8");
const history = readFileSync(new URL("../src/integration/HistoryLoads.tsx", import.meta.url), "utf8");

test("remaining routes use shared paged controls and role access", () => {
  const routes = readFileSync(new URL("../src/routes.tsx", import.meta.url), "utf8");
  assert.doesNotMatch(routes, /integration\/ComingSoon/);
  for (const screen of [connections, health, reconciliation, history]) {
    assert.match(screen, /<RoleAccess/);
    assert.match(screen, /<PagedGrid/);
  }
  assert.match(adminCommon, /<DataGrid/);
  assert.match(adminCommon, /continuation: result\?\.nextCursor/);
  assert.match(adminCommon, /controller\.abort\(\)/);
  assert.match(adminCommon, /\.\.\.source\.gridOptions/);
});

test("connection grids send header direction and selected row count to the server", async () => {
  const original = globalThis.fetch; const calls: string[] = [];
  globalThis.fetch = (async input => {
    calls.push(String(input));
    return new Response(JSON.stringify({ items: [], nextCursor: null, kpis: { connections: 0, live: 0, paused: 0, receivedToday: 0, receivedLast30Days: 0, waitingToSend: 0, openDeadLetters: 0, openManualTasks: 0, connectionsNeedingAttention: 0, relayConnected: 0 } }), { status: 200, headers: { "content-type": "application/json" } });
  }) as typeof fetch;
  try {
    const screenDefault = { key: "registration_order", dir: "asc" } as const;
    const ascending = nextGridSort(screenDefault, { key: "name", defaultDir: "asc" }, screenDefault);
    const descending = nextGridSort(ascending, { key: "name", defaultDir: "asc" }, screenDefault);
    await adminClient.connections(undefined, false, undefined, { sort: ascending, pageSize: 12 });
    await adminClient.connections(undefined, false, undefined, { sort: descending, pageSize: 12 });
    await adminClient.connections(undefined, false, undefined, { sort: descending, pageSize: 50 });
  } finally { globalThis.fetch = original; }
  assert.match(calls[0]!, /sort=name&dir=asc&take=12/);
  assert.match(calls[1]!, /sort=name&dir=desc&take=12/);
  assert.match(calls[2]!, /sort=name&dir=desc&take=50/);
  assert.match(connections, /gridId: "integration-connections"/);
  assert.match(connections, /label: "Connection"[^\n]+sort: \{ key: "name"/);
  assert.doesNotMatch(connections, /label: "Registration order"/);
});

test("site relay grid sends declared sort and page size", async () => {
  const original = globalThis.fetch; let call = "";
  globalThis.fetch = (async input => { call = String(input); return new Response(JSON.stringify({ items: [], nextCursor: null }), { status: 200, headers: { "content-type": "application/json" } }); }) as typeof fetch;
  try { await adminClient.relays("connection-fixture", undefined, undefined, { sort: { key: "status", dir: "desc" }, pageSize: 25 }); }
  finally { globalThis.fetch = original; }
  assert.match(call, /sort=status&dir=desc&take=25/);
  assert.match(settings, /gridId: "integration-site-relays"/);
  assert.match(settings, /label: "Status"[^\n]+sort: \{ key: "status"/);
});

test("service-manager status uses its read-only endpoint without operator details", () => {
  assert.match(health, /const readOnly = !operator/);
  assert.match(health, /readOnly \? <TenantHealth \/> : <OperatorHealth \/>/);
  assert.match(health, /adminApi\.status/);
  const tenant = health.slice(health.indexOf("function TenantHealth"), health.indexOf("function Refresh"));
  assert.doesNotMatch(tenant, /adminApi\.(detail|events|health|smoke|archive)/);
});

test("one-time secrets are cleared after submission and never persisted in browser storage", () => {
  assert.match(connections, /setSecrets\(\{\}\); void action\.run/);
  assert.match(settings, /Store it now/);
  assert.match(settings, /setIssued\(null\)/);
  assert.match(settings, /navigator\.clipboard\.writeText\(issued\.secret\)/);
  for (const screen of [connections, settings]) assert.doesNotMatch(screen, /localStorage|sessionStorage|console\./);
});

test("archive force and governed approval bypass require explicit evidence", () => {
  assert.equal(approvalNeedsAcceptance(false, []), true);
  assert.equal(approvalNeedsAcceptance(true, ["Customer approval"]), true);
  assert.equal(approvalNeedsAcceptance(true, []), false);
  assert.match(connections, /!report\.archived && admin/);
  assert.match(connections, /disabled=\{action\.busy \|\| !reason\.trim\(\)\}/);
  assert.match(settings, /!accepted \|\| !evidence\.trim\(\)/);
  assert.doesNotMatch(adminApi, /acceptedBy:|acceptedAt:|actorRef:/);
});

test("getting-live and connection settings expose the warranty contract rules", () => {
  assert.match(connections, /Warranty contracts are maintained in/);
  assert.match(connections, /The incumbent until cutover/);
  assert.match(connections, /item\.manualConfirmation/);
  assert.match(settings, /Warranty contract maintenance/);
  assert.match(settings, /equipment-card contract fields are always watched/);
  assert.match(settings, /adminApi\.saveWarrantyContracts/);
  assert.match(adminApi, /settings\/warranty-contracts/);
  assert.match(adminApi, /connections\/\$\{id\}\/warranty-contracts|\$\{connection\(id\)\}\/warranty-contracts/);
});

test("connections enables pushed native warranty fields only when the connector declares support", () => {
  assert.match(connections, /value="pushed" disabled=\{!value\.pushedModeAvailable\}/);
  assert.match(connections, /Pushed sends the module’s current status/);
  assert.match(connections, /adminApi\.saveCoexistence/);
  assert.match(adminApi, /\$\{connection\(id\)\}\/coexistence/);
});

test("connection onboarding exposes start modes, certificate confirmation and guarded re-baselining", () => {
  for (const label of ["Start reading changes from", "Today", "A date", "Full history", "Read items and business partners in full", "Server certificate", "Fetch certificate", "Confirm pin", "Clear pin", "Re-baseline reading", "Re-baseline and skip the gap"])
    assert.match(connections, new RegExp(label));
  assert.match(connections, /<FormActions/);
  assert.match(connections, /adminApi\.fullHistoryEstimate/);
  assert.match(connections, /adminApi\.saveOnboarding/);
  assert.match(connections, /adminApi\.fetchCertificate/);
  assert.match(connections, /adminApi\.rebaseline/);
  for (const path of ["/onboarding", "/full-history-estimate", "/fetch-certificate", "/rebaseline"])
    assert.ok(adminApi.includes(path), path);
});

test("connection stop and resume dialogs are administrator-only and keep their server contracts", () => {
  assert.match(connections, /admin && !isArchived && <FormActions>/);
  assert.match(connections, /detail\.sync\.state === "running"/);
  for (const label of ["Stop sync", "Resume sync", "Reason", "Note (optional)", "Outbound messages waiting", "Remote notifications"])
    assert.match(connections, new RegExp(label.replace(/[()]/g, "\\$&")));
  assert.match(connections, /syncDialog === "stop" && <ActionDialog title="Stop sync"/);
  assert.match(connections, /disabled=\{syncAction\.busy \|\| !stopReason\.trim\(\)\}/);
  assert.match(connections, /required maxLength=\{2000\}/);
  assert.match(connections, /syncDialog === "resume" && <ActionDialog title="Resume sync"/);
  assert.match(connections, /maxLength=\{2000\} value=\{resumeNote\}/);
  assert.match(connections, /<Icon name="x" \/>Stop sync/);
  assert.match(connections, /<Icon name="cycle" \/>Resume sync/);
  assert.match(adminApi, /stopSync:.*\/stop-sync.*body: \{ reason \}/);
  assert.match(adminApi, /resumeSync:.*\/resume-sync.*note: note\?\.trim\(\) \|\| null/);
  assert.doesNotMatch(adminApi, /actor|stoppedBy|resumedBy/i);
});

test("connection detail and health render running stopped and catching-up states with detail links", () => {
  for (const screen of [connections, health]) {
    assert.match(screen, /sync\.state === "running"/);
    assert.match(screen, /Stopped since/);
    assert.match(screen, /Catching up since/);
  }
  assert.match(health, /to=\{`\/integration\/connections\?connection=\$\{x\.connectionId\}`\}/g);
  assert.match(health, /syncLabel\(x\.sync\)/g);
});

test("connection health shows classified failures for the connection and each entity", () => {
  assert.match(health, /health\.lastErrorCode/);
  assert.match(health, /x\.lastErrorCode/);
  assert.match(health, /x\.lastErrorDetail/);
});

test("reconciliation details have independent server continuations and location filter", () => {
  assert.match(adminApi, /\/entity-days\$\{query\(\{ cursor, take: 12 \}\)\}/);
  assert.match(adminApi, /\/locations\$\{query\(\{ cursor, take: 12, differences \}\)\}/);
  assert.match(reconciliation, /sourceKeyFieldHash === x\.moduleKeyFieldHash|moduleKeyFieldHash === x\.sourceKeyFieldHash/);
  assert.match(history, /job\.savedCursor/);
  assert.match(history, /Confirm cancellation/);
});


test("UTC timestamps render consistently with or without the SQL timezone suffix", () => {
  assert.equal(localDate("2026-09-01T09:00:00"), localDate("2026-09-01T09:00:00Z"));
  const now = Date.parse("2026-09-01T09:30:00Z");
  assert.equal(age("2026-09-01T09:00:00", now), "30 min");
  assert.equal(age("2026-09-01T09:00:00Z", now), "30 min");
});


test("daily check ranges use the inclusive connection window and reject days after the connection's today", () => {
  const today = "2026-09-10";
  const boundary = reconciliationRange("2026-09-01", "2026-09-07", 7, today);
  assert.equal(boundary.error, null);
  assert.equal(boundary.startMin, "2026-09-01");
  assert.equal(boundary.endMax, "2026-09-07");
  assert.match(reconciliationRange("2026-09-01", "2026-09-08", 7, today).error!, /at most 7 days/);
  assert.equal(reconciliationRange("2026-09-01", "2026-09-08", 8, today).error, null);
  assert.match(reconciliationRange("2026-09-10", "2026-09-11", 7, today).error!, /after today/);
  assert.equal(reconciliationRange("2026-09-10", "2026-09-10", 1, today).error, null);
  assert.match(reconciliationRange("2026-09-02", "2026-09-01", 7, today).error!, /precede/);
  assert.equal(reconciliationRange("2026-08-30", "2026-09-01", 3, today).error, null);
  assert.equal(reconciliationRange("2026-09-09", "", 7, today).endMax, today);
});
