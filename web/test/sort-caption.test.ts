import assert from "node:assert/strict";
import test from "node:test";
import { sortCaption } from "../src/components/sortCaption.ts";

const columns = [{ label: "Claim", sort: { key: "number" } }, { label: "Age", sort: { key: "submitted" } }, { label: "Serial" }];
const defaultSort = { key: "submitted", dir: "asc" as const };

test("the screen's own wording stays while its default order applies", () => {
  assert.equal(sortCaption(columns, { sort: defaultSort, defaultSort }, "Sorted by age, oldest first"), "Sorted by age, oldest first");
});

test("after a re-sort the caption names the active column and direction", () => {
  assert.equal(sortCaption(columns, { sort: { key: "number", dir: "asc" }, defaultSort }, "Sorted by age, oldest first"), "Sorted by claim, ascending");
  assert.equal(sortCaption(columns, { sort: { key: "submitted", dir: "desc" }, defaultSort }, "Sorted by age, oldest first"), "Sorted by age, descending");
});

test("an unknown sort key falls back to the screen's wording", () => {
  assert.equal(sortCaption(columns, { sort: { key: "gone", dir: "asc" }, defaultSort }, "oldest first"), "oldest first");
});
