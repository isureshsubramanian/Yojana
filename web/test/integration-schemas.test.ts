import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import * as z from "zod/mini";
import { connectionPage, currencyMap, cursorPage, tenantStatusPage } from "../src/integration/adminTypes.ts";
import { migrationSourcePage } from "../src/integration/migrationTypes.ts";
import { deadLetterDetail, deadLetterRow, manualTask } from "../src/integration/types.ts";

// ADR-0019 Integration batch: the schemas follow the C# views the Integration endpoints return (camelCase on HTTP;
// the snake_case policy is for connector messages only). Values are demo-format, not real data.
const source = (path: string) => readFileSync(new URL(`../src/${path}`, import.meta.url), "utf8");
const id = (n: number) => `0199a1b2-0000-7000-8000-${String(n).padStart(12, "0")}`;

test("Integration API files make no unchecked api calls", () => {
  for (const file of ["adminApi.ts", "api.ts", "erpChangeApi.ts", "extensionApi.ts", "migrationApi.ts", "parallelRunApi.ts", "verificationApi.ts"]) {
    const text = source(`integration/${file}`);
    assert.doesNotMatch(text, /\bapi(<[^>]*>)?\(/, file);
    assert.match(text, /apiParsed\(/, file);
  }
});

const row = {
  deadLetterItemId: id(1), connectionId: id(2), connectionLabel: "ERP production", direction: "inbound", recordType: "delivery",
  recordReference: "DN-40211", reasonCode: "unknown_item", failure: "Item SRV-R760 is not known", status: "open", attemptCount: 3,
  createdAt: "2026-10-24T08:00:00Z", updatedAt: "2026-10-24T08:05:00Z",
};

test("a dead-letter detail parses without the list-only recordReference and attemptCount (the C# detail has neither)", () => {
  const { recordReference: _reference, attemptCount: _attempts, ...common } = row;
  const detail = {
    ...common, sourceKind: "inbox", sourceId: id(3), rawPayload: "{}", canonicalAttempt: "{}", resolvedAt: null,
    events: [{ deadLetterEventId: id(4), action: "parked", reason: null, detailsJson: "{}", occurredAt: "2026-10-24T08:00:00Z" }], manualTask: null,
  };
  assert.equal(z.parse(deadLetterDetail, detail).events.length, 1);
  assert.equal(z.safeParse(deadLetterRow, { ...row, attemptCount: undefined }).success, false);
});

test("a manual task that did not come from a dead letter carries null source ids", () => {
  const task = {
    manualOutboundTaskId: id(5), deadLetterItemId: null, outboundCommandId: null, connectionId: id(2), connectionLabel: "ERP production",
    commandType: "post_delivery", desk: "logistics", draft: "{}", status: "waiting", createdAt: "2026-10-24T09:00:00Z",
    erpExternalId: null, erpChangeMarker: null, completedBy: null, completedAt: null,
  };
  assert.equal(z.parse(manualTask, task).deadLetterItemId, null);
});

test("cursor pages share one shape, and the connection pages add their KPIs", () => {
  const keys = (schema: { def: { shape: Record<string, unknown> } }) => Object.keys(schema.def.shape).sort();
  assert.deepEqual(keys(cursorPage(z.string())), ["items", "nextCursor"]);
  assert.deepEqual(keys(migrationSourcePage), ["items", "nextCursor"]);
  assert.deepEqual(keys(connectionPage), ["items", "kpis", "nextCursor"]);
  assert.deepEqual(keys(tenantStatusPage), ["items", "kpis", "nextCursor"]);
  assert.equal(z.safeParse(migrationSourcePage, { items: [], nextCursor: null }).success, true);
});

test("CX01 currency settings keep suggestions distinct from confirmed mappings", () => {
  const parsed = z.parse(currencyMap, { connectionId: id(2), surveyObservedAt: "2026-09-01T09:00:00Z",
    currencies: [{ erpCode: "$", name: "US Dollar", documentsCode: "$", suggestedIsoCode: "USD", mappedIsoCode: null }] });
  assert.equal(parsed.currencies[0]!.suggestedIsoCode, "USD");
  assert.equal(parsed.currencies[0]!.mappedIsoCode, null);
});
