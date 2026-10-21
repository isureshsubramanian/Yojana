import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
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
