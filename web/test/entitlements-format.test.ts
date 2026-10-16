import { test } from "node:test";
import assert from "node:assert/strict";
import { commaList, daysOld, entitlementTone, gapCause, groupEntitlements, nullableNumber } from "../src/entitlements/format.ts";
import { approvalId, parseApprovals, upsertApproval } from "../src/entitlements/approvalStore.ts";
import type { EntitlementView, GapRow, StoredApproval } from "../src/entitlements/types.ts";

const entitlement = (effectiveStatus: string): EntitlementView => ({ entitlementId: crypto.randomUUID(), unitId: crypto.randomUUID(), coverageFamily: "base_warranty", direction: "outbound", type: "manufacturer", storedStatus: effectiveStatus, effectiveStatus, closedReason: null, startDate: null, endDate: null, daysRemaining: null, policyVersionId: crypto.randomUUID(), startBasis: null, startTimeZone: "UTC", parentEntitlementId: null, rootEntitlementId: null, supersedesEntitlementId: null });

test("entitlement presentation helpers preserve state semantics", () => {
  const active = entitlement("active");
  const expired = entitlement("expired");
  assert.deepEqual(groupEntitlements([active, expired]), { live: [active], history: [expired] });
  assert.equal(entitlementTone("pending_evidence"), "warn");
  assert.equal(entitlementTone("active"), "ok");
  assert.deepEqual(commaList("EU, GB\nEU"), ["EU", "GB"]);
  assert.equal(nullableNumber(""), null);
  assert.equal(nullableNumber("12.5"), 12.5);
});

test("gap formatters name the backend cause and compute whole days", () => {
  const row = { missingInput: "no_policy", requirementStatus: "exception" } as GapRow;
  assert.equal(gapCause(row), "no_policy");
  assert.equal(daysOld("2026-10-15T00:00:00Z", Date.parse("2026-10-17T12:00:00Z")), 2);
});

test("approval store rejects malformed JSON and replaces an existing selection", () => {
  assert.deepEqual(parseApprovals("not json"), []);
  const base = { id: "old", selectionId: "s", operation: "entitlement.amend", source: { kind: "pasted_list" }, arguments: {} as StoredApproval["arguments"], blocked: [], total: 1, createdAt: "2026-10-17T00:00:00Z" } satisfies StoredApproval;
  const next = { ...base, id: approvalId("s", "entitlement.amend"), total: 2 };
  assert.deepEqual(upsertApproval([base], next), [next]);
});
