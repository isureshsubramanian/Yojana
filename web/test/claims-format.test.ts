import { test } from "node:test";
import assert from "node:assert/strict";
import { ageBucketLabel, humanise, money, moneyTotals, stackOutcome } from "../src/claims/format.ts";

const catalogue = [
  { code: "JPY", name: "Japanese Yen", symbol: "¥", minorDigits: 0 },
  { code: "KWD", name: "Kuwaiti Dinar", symbol: "د.ك", minorDigits: 3 },
  { code: "GBP", name: "Pound Sterling", symbol: "£", minorDigits: 2 },
];

test("claim money preserves currency boundaries and catalogue minor digits", () => {
  assert.match(money(1234.5, "JPY", catalogue), /1,235/);
  assert.match(money(12.3456, "KWD", catalogue), /12\.346/);
  const totals = moneyTotals([{ currency: "GBP", amount: 20 }, { currency: "JPY", amount: 500 }], catalogue);
  assert.match(totals, /20\.00/);
  assert.match(totals, /500/);
  assert.match(totals, / · /);
});

test("claim labels humanise reasons, ageing buckets and policy outcomes", () => {
  assert.equal(humanise("unauthorised_parts_damage"), "Unauthorised parts damage");
  assert.deepEqual(Array.from({ length: 5 }, (_, index) => ageBucketLabel(index)), ["Under 2 days", "2–5 days", "6–10 days", "11–20 days", "Over 20 days"]);
  assert.equal(stackOutcome({ outcome: "matched" }), "Matched");
  assert.equal(stackOutcome({ outcome: "no_match" }), "No match");
  assert.equal(stackOutcome({ outcome: "not_reached" }), "Not reached");
});
