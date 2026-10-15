import { test } from "node:test";
import assert from "node:assert/strict";
import { windowRows } from "../src/components/windowing.ts";
import { pageCount, rowRange } from "../src/components/paging.ts";

test("windowing keeps only the viewport plus overscan in the DOM and pads the rest", () => {
  const w = windowRows(500, 44, 44 * 100, 440, 6);
  assert.equal(w.start, 94);
  assert.equal(w.end, 94 + 10 + 12);
  assert.equal(w.topPad, 94 * 44);
  assert.equal(w.bottomPad, (500 - w.end) * 44);
});

test("windowing covers every row of a short page and nothing of an empty one", () => {
  assert.deepEqual(windowRows(0, 44, 0, 600), { start: 0, end: 0, topPad: 0, bottomPad: 0 });
  const w = windowRows(5, 44, 0, 600);
  assert.equal(w.start, 0);
  assert.equal(w.end, 5);
  assert.equal(w.bottomPad, 0);
});

test("paging arithmetic", () => {
  assert.equal(pageCount(1_284_317, 50), 25_687);
  assert.deepEqual(rowRange(1, 50, 1_284_317), { from: 1, to: 50 });
  assert.deepEqual(rowRange(25_687, 50, 1_284_317), { from: 1_284_301, to: 1_284_317 });
  assert.deepEqual(rowRange(1, 50, 0), { from: 0, to: 0 });
});
