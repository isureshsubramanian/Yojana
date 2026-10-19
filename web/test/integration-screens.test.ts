import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import { mayOperateIntegration, maySeeIntegrationIndicator, reasonLabel } from "../src/integration/model.ts";

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
