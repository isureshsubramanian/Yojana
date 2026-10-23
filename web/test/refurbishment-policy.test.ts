import assert from "node:assert/strict";
import test from "node:test";
import { duplicateRows, policyProblems } from "../src/settings/refurbishmentPolicyModel.ts";
import type { RefurbishmentPolicy } from "../src/refurbishments/types.ts";

const policy = (classes: string[]): RefurbishmentPolicy => ({
  defaultIdentity: "same_unit_new_serial", defaultMaxCycles: 3, defaultReplacementPolicyCode: null, items: null,
  itemClasses: classes.map((itemClass) => ({ itemClass, identity: "same_unit_same_serial", maxCycles: 2, replacementPolicyCode: null })),
});

test("a repeated item class marks the later row and says why Save is unavailable", () => {
  const value = policy(["storage", "printer", " Storage "]);
  const duplicates = duplicateRows(value);
  assert.deepEqual([...duplicates], [2]);
  assert.deepEqual(policyProblems(value, duplicates), ["Storage is overridden more than once. Remove the duplicate row or change its item class."]);
});

test("distinct item classes raise no problem", () => {
  const value = policy(["storage", "printer"]);
  assert.deepEqual(policyProblems(value, duplicateRows(value)), []);
});
