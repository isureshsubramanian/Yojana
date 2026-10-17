import { test } from "node:test";
import assert from "node:assert/strict";
import { ageBucketLabel, campaignCoverNote, campaignCoverTerms, campaignOptionHint, coverageSourceLabel, humanise, money, moneyTotals, stackOutcome } from "../src/claims/format.ts";
import type { ClaimCampaignOption } from "../src/claims/types.ts";

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

const endedRecall: ClaimCampaignOption = {
  campaignId: "11111111-1111-1111-1111-111111111111", code: "RC-26-04", name: "Valve inspection",
  coverageSource: "recall", reserveTag: "RSV-RC-26-04", reasonCode: "safety", runsUntil: "2026-09-30",
  partsPct: 100, labourPct: 50, travelPct: 0,
  coversOnDate: false, notCoveredReason: "campaign_ended",
};

test("campaign claim guidance uses plain English reasons", () => {
  assert.equal(campaignOptionHint([], "recall"), "Recall needs an active campaign selecting this unit; none does.");
  assert.equal(campaignOptionHint([endedRecall], "recall"), "RC-26-04 ended on 2026-09-30; a claim dated after it is decided under normal warranty.");
  assert.equal(campaignCoverNote("RC-26-04", "campaign_ended", "2026-09-30"), "Named campaign RC-26-04 gave no cover: ended on 2026-09-30 — lines decided under the policy stack.");
  assert.equal(campaignCoverNote("GW-26-02", "campaign_closed", null), "Named campaign GW-26-02 gave no cover: was closed — lines decided under the policy stack.");
  assert.equal(coverageSourceLabel("recall", "RC-26-04"), "Recall · RC-26-04");
  assert.equal(campaignCoverTerms(endedRecall), "covers parts 100% · labour 50% · travel 0%");
  assert.equal(campaignCoverTerms({ partsPct: 100, labourPct: 100, travelPct: 100 }), null);
});
