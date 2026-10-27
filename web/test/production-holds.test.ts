import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import { assignProductionComponents, inProductionDetail, inProductionOrderPage } from "../src/units/productionApi.ts";

const id = (n: number) => `0199a1b2-0000-7000-8000-${String(n).padStart(12, "0")}`;
const document = (n: number) => ({ type: "production_order", id: id(n), line: null });

test("in-production schemas accept server pages and validate the assignment scan", () => {
  assert.equal(inProductionOrderPage.safeParse({ items: [{ productionOrder: document(1), producedItemId: id(2),
    heldCount: 25, oldestIssuedAt: "2026-09-01T08:00:00Z", parentsReceived: 1,
    status: "waiting_for_receipt", continuation: null }], nextCursor: null }).success, true);
  assert.equal(inProductionDetail.safeParse({ productionOrder: document(1), producedItemId: id(2), components: [{
    holdId: id(3), unitId: id(4), itemId: id(5), serial: "NIC-25G-00041", issuedAt: "2026-09-01T08:00:00Z",
    issue: { type: "inventory_issue", id: id(6), line: 1 }, issueLine: 1,
  }], parents: [], status: "waiting_for_receipt" }).success, true);
  assert.equal(assignProductionComponents.safeParse({ commandId: id(7), productionOrder: document(1),
    parentSerial: "SRV-RACK-00014", componentSerials: [] }).success, false);
  assert.equal(assignProductionComponents.safeParse({ commandId: id(7), productionOrder: document(1),
    parentSerial: "SRV-RACK-00014", componentSerials: ["NIC-25G-00041"] }).success, true);
});

test("in-production screen is server-paged and keeps assignment as an explicit commit action", () => {
  const screen = readFileSync(new URL("../src/units/InProduction.tsx", import.meta.url), "utf8");
  assert.match(screen, /useGridQuery\("\/api\/production-holds"/);
  assert.match(screen, /<DataGrid/);
  assert.match(screen, /<FormActions><button type="submit"/);
  assert.match(screen, /Assign components/);
  assert.doesNotMatch(screen, /actorRef/);
});

test("unit history and as-built show the production source wording", () => {
  const detail = readFileSync(new URL("../src/units/UnitDetail.tsx", import.meta.url), "utf8");
  assert.match(detail, /In production · order/);
  assert.match(detail, /Part of \$\{production\.parentSerial/);
  assert.match(detail, /issue \$\{referenceNames\.document\(production\.issue\)\}/);
  assert.match(detail, /Source:/);
  assert.match(detail, /receipt \$\{referenceNames\.document\(source\.receipt\)\}/);
});
