import { test } from "node:test";
import assert from "node:assert/strict";
import { ageBucket, dispositionOptionState, gradeOptionState, reasonEffect, receiptValuationRule, valuationRuleForGrade } from "../src/returns/format.ts";
import type { RmaDetailLine } from "../src/returns/types.ts";

const line = { receivedUnitId: "unit", receiptOutcome: "accepted", conditionGrade: "defective", replacementIntentId: null } as RmaDetailLine;

test("RMA age buckets retain their boundary days", () => {
  assert.equal(ageBucket(2), "fresh"); assert.equal(ageBucket(3), "watch"); assert.equal(ageBucket(7), "watch"); assert.equal(ageBucket(8), "aged");
});

test("disposition options explain why an exception blocks the command", () => {
  assert.deepEqual(dispositionOptionState("refurbish", { ...line, receiptOutcome: "mismatch" }, "received"), { enabled: false, reason: "Resolve the mismatch exception first" });
  assert.equal(dispositionOptionState("refurbish", line, "received").enabled, true);
  assert.equal(dispositionOptionState("restock_as_new", line, "received").enabled, false);
});

test("receipt grade helpers keep release-only grades disabled and select valuation rules", () => {
  assert.equal(gradeOptionState("refurbished_a").enabled, false);
  assert.equal(gradeOptionState("used").enabled, true);
  assert.equal(valuationRuleForGrade("defective"), "write_down_to_salvage");
  assert.deepEqual(receiptValuationRule({ receiptBusinessTime: null, valuationRule: null }, "defective"),
    { rule: "write_down_to_salvage", proposed: true });
  assert.deepEqual(receiptValuationRule({ receiptBusinessTime: "2026-10-18T09:15:00Z", valuationRule: "server_adjusted" }, "new"),
    { rule: "server_adjusted", proposed: false });
});

test("replacement reason effects state the warranty arithmetic", () => {
  assert.match(reasonEffect("dead_on_arrival"), /start and term kept/);
  assert.match(reasonEffect("seller_error"), /not a warranty cost/);
});
