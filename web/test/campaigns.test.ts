import assert from "node:assert/strict";
import test from "node:test";
import { campaignSchedule, campaignTone, pageNumber, repairPresentation, title } from "../src/campaigns/format.ts";

test("campaign status presentation remains text-led", () => {
  assert.equal(campaignTone("active"), "ok");
  assert.equal(campaignTone("draft"), "warn");
  assert.equal(title("partially_approved"), "Partially approved");
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
