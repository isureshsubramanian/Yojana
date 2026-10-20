import assert from "node:assert/strict";
import test from "node:test";
import { readFileSync } from "node:fs";

test("policy editor exposes the tenant-default placeholder and locks published waiting periods", () => {
  const source = readFileSync(new URL("../src/entitlements/PolicyEditor.tsx", import.meta.url), "utf8");
  assert.match(source, /No claim within N days of purchase/);
  assert.match(source, /placeholder=\{String\(detail\.waitingPeriodDefaultDays\)\}/);
  assert.match(source, /noClaimWithinDaysOfPurchase: nullableNumber/);
  assert.match(source, /disabled=\{!editable\}/);
});

test("adjudication uses the readable waiting-period label with both dates", () => {
  const source = readFileSync(new URL("../src/claims/ClaimAdjudication.tsx", import.meta.url), "utf8");
  assert.match(source, /denialReasonLabel\(line\.denialReason, line\.waitingPeriodPurchaseDate, line\.firstClaimableDate\)/);
  assert.doesNotMatch(source, />\{line\.denialReason\}</);
});
