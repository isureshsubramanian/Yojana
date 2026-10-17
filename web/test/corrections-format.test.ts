import { test } from "node:test";
import assert from "node:assert/strict";
import { identifierLines, serialSelection, slugChoice } from "../src/corrections/format.ts";
import type { WorkbenchChoice } from "../src/corrections/types.ts";

test("allocation serial input keeps ranges distinct from pasted lists", () => {
  assert.deepEqual(serialSelection("2510-007713 → 2510-007740", "VX"), { kind: "serial_range", scheme: "VX", rangeStart: "2510-007713", rangeEnd: "2510-007740" });
  assert.deepEqual(serialSelection("A-1\nA-2, A-3", "VX"), { kind: "pasted_list", serials: ["A-1", "A-2", "A-3"] });
  assert.equal(serialSelection("", "VX"), null);
});

test("vendor list parsing validates explicit unit pairs", () => {
  const id = "3fa85f64-5717-4562-b3fc-2c963f66afa6";
  assert.deepEqual(identifierLines("RX-1\nRX-2", false), { lines: [{ value: "RX-1" }, { value: "RX-2" }], errors: [] });
  assert.deepEqual(identifierLines(`${id}, RX-1\nnot-a-unit, RX-2`, true), { lines: [{ unitId: id, value: "RX-1" }], errors: ["Line 2 needs a unit UUID, then a serial value."] });
});

test("administrator choice ids are stable and collision-safe", () => {
  const choice = { choice: "demo_unit", label: "", area: "", helpText: "", commands: [], questions: [], defaultReason: "", rootCause: 1, searchWords: null, order: 1, initiateRoles: null, commitRoles: null, status: "shown", standard: false, fallback: false } satisfies WorkbenchChoice;
  assert.equal(slugChoice("Demo unit", [choice]), "demo_unit_2");
  assert.equal(slugChoice("  Calibration label! ", []), "calibration_label");
});
