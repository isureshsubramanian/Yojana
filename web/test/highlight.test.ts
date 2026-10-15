import { test } from "node:test";
import assert from "node:assert/strict";
import { flatten, splitHighlights, step } from "../src/components/search/highlight.ts";

test("match spans split a title into plain and highlighted pieces", () => {
  assert.deepEqual(splitHighlights("2506-493445", [{ start: 5, length: 6 }]), [{ text: "2506-", hit: false }, { text: "493445", hit: true }]);
  assert.deepEqual(splitHighlights("abc", []), [{ text: "abc", hit: false }]);
  assert.deepEqual(splitHighlights("abc", [{ start: 2, length: 10 }]), [{ text: "ab", hit: false }, { text: "c", hit: true }]);
});

test("keyboard navigation wraps across groups", () => {
  const all = flatten([{ results: [1, 2] }, { results: [3] }]);
  assert.deepEqual(all, [1, 2, 3]);
  assert.equal(step(2, 3, 1), 0);
  assert.equal(step(0, 3, -1), 2);
  assert.equal(step(0, 0, 1), -1);
});
