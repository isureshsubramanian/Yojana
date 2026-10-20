import assert from "node:assert/strict";
import test from "node:test";
import { readFileSync } from "node:fs";
import { changedFamilies, changeLabel, coverQuery, moneyLabel } from "../src/entitlements/coverModel.ts";
import type { CoverFamily } from "../src/entitlements/coverTypes.ts";

test("money preserves separate currencies and missing refund amounts", () => {
  assert.equal(moneyLabel([{ currency: "USD", amount: 180 }, { currency: "EUR", amount: 640 }]), "USD 180 · EUR 640");
  assert.equal(moneyLabel([]), "No amount recorded");
  assert.match(moneyLabel([{ currency: "USD", amount: -20 }]), /-20/);
});
test("changed-only comparisons exclude natural expiry, transfers and not-taken cover", () => {
  const rows = ["added", "upgraded", "amended", "ended", "transferred", "not_taken", "same"].map((change) => ({ change } as CoverFamily));
  assert.equal(changedFamilies(rows, false).length, 7);
  assert.deepEqual(changedFamilies(rows, true).map((r) => r.change), ["added", "upgraded", "amended"]);
  assert.equal(changeLabel("closed_early"), "Closed early");
});
test("filter and cursor contract keeps the opaque cursor and names out of identity parameters", () => {
  const q = new URLSearchParams(coverQuery({ serial: "RS 1100", itemFamily: "rack_servers", soldFrom: "2025-11-04" }, "a+b="));
  assert.equal(q.get("serial"), "RS 1100");
  assert.equal(q.get("cursor"), "a+b=");
  assert.equal(q.has("actorRef"), false);
});
test("unit screen has comparison, change selection, full named record and no identifier fallbacks", () => {
  const src = readFileSync(new URL("../src/entitlements/CoverSinceSalePanel.tsx", import.meta.url), "utf8");
  for (const text of ["At a glance", "At sale and today, by cover", "Changes since sale", "No sale recorded", "Before and after", "Approved by", "Payer", "Changed only"]) assert.ok(src.includes(text));
  assert.doesNotMatch(src, /shortId|actorRef|\.slice\(0,\s*\d+\)/);
  assert.match(src, /controller\.abort/);
});
test("report uses server cursors, shared grid and exports with by-kind card before unit list", () => {
  const src = readFileSync(new URL("../src/entitlements/CoverChanges.tsx", import.meta.url), "utf8");
  assert.match(src, /cursorStack/);
  assert.match(src, /data\.nextCursor/);
  assert.match(src, /DataGrid/);
  assert.match(src, /ExportButtons/);
  assert.match(src, /PartyField/);
  assert.ok(src.indexOf("<h2>What changed") < src.indexOf("<h2>Units"));
  assert.doesNotMatch(src, /shortId|actorRef/);
  assert.match(src, /100000/);
});
