import { test } from "node:test";
import assert from "node:assert/strict";
import { identityOutcome, identityTitle, releaseBlocked } from "../src/refurbishments/format.ts";

test("refurbishment identity wording explains all three server policies", () => {
  assert.equal(identityTitle("same_unit_same_serial"), "Same unit · same serial");
  assert.equal(identityTitle("same_unit_new_serial"), "Same unit · new serial with lineage");
  assert.equal(identityTitle("new_unit"), "New derived unit");
  assert.match(identityOutcome("same_unit_new_serial", "OLD-1", null, false).after, /server/);
  assert.equal(identityOutcome("same_unit_same_serial", "S-1", null, false).after, "S-1");
});

test("release blocking consumes required server checks only", () => {
  assert.equal(releaseBlocked([{ key: "optional", label: "Optional", detail: "", passed: false, required: false }]), false);
  assert.equal(releaseBlocked([{ key: "test", label: "Test", detail: "", passed: false, required: true }]), true);
});
