import assert from "node:assert/strict";
import test from "node:test";
import {
  certificateDatePatterns,
  certificateDateTimeExample,
  certificateTimePatterns,
} from "../src/settings/certificateDateFormats.ts";

test("certificate date setting offers every approved fixed pattern with a live example", () => {
  assert.deepEqual(certificateDatePatterns, ["d MMM yyyy", "dd/MM/yyyy", "MM/dd/yyyy", "yyyy-MM-dd"]);
  assert.equal(certificateDateTimeExample("en", "d MMM yyyy", "HH:mm"), "18 Oct 2026, 13:45 UTC");
  assert.equal(certificateDateTimeExample("en", "dd/MM/yyyy", "HH:mm"), "18/10/2026, 13:45 UTC");
  assert.equal(certificateDateTimeExample("en-US", "MM/dd/yyyy", "h:mm tt"), "10/18/2026, 1:45 PM UTC");
  assert.equal(certificateDateTimeExample("en", "yyyy-MM-dd", "HH:mm"), "2026-10-18, 13:45 UTC");
});

test("certificate time setting presents both clock choices", () => {
  assert.deepEqual(certificateTimePatterns, [
    { value: "HH:mm", label: "24-hour" },
    { value: "h:mm tt", label: "12-hour" },
  ]);
});
