import { test } from "node:test";
import assert from "node:assert/strict";
import { dueText, filterCount, filterTitle, replacementStatus, replacementTone } from "../src/replacements/format.ts";
import type { ReplacementLinkRow } from "../src/replacements/types.ts";

const row = (patch: Partial<ReplacementLinkRow> = {}): ReplacementLinkRow => ({
  replacementLinkId: "l", warrantyReplacementId: "w", replacementNumber: "WRP-1", direction: "customer_facing", state: "awaiting_return",
  originalUnitId: "o", replacementUnitId: "r", counterpartyPartyRef: "p", returnDue: "2026-10-01", daysOverdue: 7,
  nonReturnPriceBasis: "list", nonReturnAmount: 100, currency: "GBP", overduePublishedAt: null, partyOnHold: false,
  originalSerial: "OLD-1", replacementSerial: "NEW-1", originalItemId: "oi", replacementItemId: "ri", sourceNumber: "RMA-1",
  replacementReason: "warranty_defect", shippedAt: null, ...patch,
});

test("replacement due and status wording keeps urgency in text", () => {
  assert.equal(dueText(row()), "7 d overdue");
  assert.equal(replacementStatus(row()), "Overdue");
  assert.equal(replacementTone(row()), "warn");
  assert.equal(replacementStatus(row({ partyOnHold: true })), "Customer on hold");
  assert.equal(replacementTone(row({ partyOnHold: true })), "bad");
});

test("replacement segmented filters use API KPI counts", () => {
  const kpis = { awaiting: 17, overdue: 6, dueInSevenDays: 4, customersOnHold: 2 };
  assert.equal(filterTitle("due_soon"), "Due in 7 days");
  assert.equal(filterCount("overdue", kpis), 6);
  assert.equal(filterCount("due_soon", kpis), 4);
  assert.equal(filterCount("all", kpis), 17);
});
