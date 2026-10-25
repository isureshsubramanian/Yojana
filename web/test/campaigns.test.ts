import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import { campaignCoverSummary, campaignSchedule, campaignTone, pageNumber, repairPresentation, title } from "../src/campaigns/format.ts";
import { campaignFunnelStepSchema } from "../src/campaigns/types.ts";

test("campaign status presentation remains text-led", () => {
  assert.equal(campaignTone("active"), "ok");
  assert.equal(campaignTone("draft"), "warn");
  assert.equal(title("partially_approved"), "Partially approved");
  assert.equal(campaignCoverSummary(100, 50, 0), "Cover 100 / 50 / 0 %");
});

test("repair guidance follows claim then custody", () => {
  assert.deepEqual(repairPresentation("settled", "warehouse"), { label: "Settled", tone: "ok", detail: null });
  assert.equal(repairPresentation(null, "in_repair").label, "Fix in repair");
  assert.equal(repairPresentation(null, "customer_site").label, "Open");
});

test("page number derives from server offsets", () => assert.equal(pageNumber(100, 50), 3));

test("campaign schedule ends only after the inclusive runs-until date", () => {
  assert.deepEqual(campaignSchedule("2026-10-17", "2026-10-17"), { ended: false, label: "Runs until 2026-10-17" });
  assert.deepEqual(campaignSchedule("2026-10-17", "2026-10-18"), { ended: true, label: "Ended 2026-10-17" });
  assert.deepEqual(campaignSchedule(null, "2026-10-18"), { ended: false, label: "No end date" });
});

test("campaign API schemas accept the DTO and refuse a missing required field", () => {
  assert.equal(zSafe(campaignFunnelStepSchema, { kind: "firmware", count: 14 }), true);
  assert.equal(zSafe(campaignFunnelStepSchema, { kind: "firmware" }), false);
  const source = readFileSync(new URL("../src/campaigns/api.ts", import.meta.url), "utf8");
  assert.doesNotMatch(source, /\bapi(?:<[^>]*>)?\(/);
});

function zSafe(schema: typeof campaignFunnelStepSchema, value: unknown): boolean { return schema.safeParse(value).success; }
