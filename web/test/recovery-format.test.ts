import { test } from "node:test";
import assert from "node:assert/strict";
import { currencyAmounts, noRecoveryExplanation, primaryApprovalRate, recoveryState, termText } from "../src/recovery/format.ts";
import type { RecoveryNotRaisedRow } from "../src/recovery/types.ts";

test("recovery amounts keep currencies separate and calculate the primary-currency rate", () => {
  const totals = [
    { currency: "EUR", claimCount: 4, requestedAmount: 1000, approvedAmount: 920 },
    { currency: "GBP", claimCount: 1, requestedAmount: 300, approvedAmount: 274 },
  ];
  const formatted = currencyAmounts(totals, "approvedAmount");
  assert.match(formatted.value, /920/);
  assert.match(formatted.others, /274/);
  assert.equal(primaryApprovalRate(totals), 92);
});

test("recovery state derives waiting, partial and refusal without adding stored statuses", () => {
  assert.deepEqual(recoveryState({ status: "raised", ageDays: 14, approvedAmount: null, requestedAmount: 100, vendorNote: null }, 10), { label: "Waiting", tone: "warn", detail: "14 d" });
  assert.equal(recoveryState({ status: "raised", ageDays: 8, approvedAmount: null, requestedAmount: 100, vendorNote: null }, 10).label, "Raised");
  assert.equal(recoveryState({ status: "approved", ageDays: 2, approvedAmount: 80, requestedAmount: 100, vendorNote: "labour refused" }, 10).label, "Partially approved");
  assert.equal(recoveryState({ status: "approved", ageDays: 2, approvedAmount: 0, requestedAmount: 100, vendorNote: null }, 10).label, "Refused");
});

test("term and not-raised explanations mirror the approved wording", () => {
  assert.equal(termText(12, "2026-07-02"), "12 m · ends 2026-07-02");
  const row = { reason: "vendor_term_ended", vendorTermMonths: 12, vendorTermEnd: "2026-07-02", claimMonthOfTerm: 15 } as RecoveryNotRaisedRow;
  assert.equal(noRecoveryExplanation(row.reason, row), "vendor term 12 m ended 2026-07-02 · claim in month 15");
});

test("policy reasons explain why a live vendor term did not raise recovery", () => {
  assert.equal(noRecoveryExplanation("campaign_claim"), "Claim under a campaign; tenant policy recovers nothing from suppliers for campaign claims.");
  assert.equal(noRecoveryExplanation("several_covered_parts"), "several parts inside the unit carry live vendor terms · name the part on the line to recover");
  assert.equal(noRecoveryExplanation("part_not_named"), "policy recovers only from a named part · the line names none");
  assert.equal(noRecoveryExplanation("line_kind_not_recovered"), "the approved line kinds are not recovered under the tenant policy");
});
