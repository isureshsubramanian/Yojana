import { test } from "node:test";
import assert from "node:assert/strict";
import { humanise, matchSpan, parseDocumentParam, serialFromScan, splitSerials, statusLabel, statusTone, documentLabel, fmtDateTime } from "../src/units/format.ts";

test("status labels and tones follow the lifecycle vocabulary", () => {
  assert.equal(statusLabel("pre_release"), "Pre-release");
  assert.equal(statusLabel("released"), "Released");
  assert.equal(statusTone("released"), "ok");
  assert.equal(statusTone("scrapped_pre_release"), "bad");
  assert.equal(statusTone("pending_identifier"), "warn");
  assert.equal(humanise("custody.changed"), "custody changed");
});

test("document links parse as type:guid and nothing else", () => {
  assert.deepEqual(parseDocumentParam("delivery:3fa85f64-5717-4562-b3fc-2c963f66afa6"), { type: "delivery", id: "3fa85f64-5717-4562-b3fc-2c963f66afa6" });
  assert.equal(parseDocumentParam("delivery:not-a-guid"), null);
  assert.equal(parseDocumentParam(":3fa85f64-5717-4562-b3fc-2c963f66afa6"), null);
  assert.equal(documentLabel({ type: "sales_order", id: "3fa85f64-5717-4562-b3fc-2c963f66afa6", line: 4 }), "sales order 3FA85F64 line 4");
});

test("pasted serials split on any separator, keep order, drop blanks and duplicates", () => {
  assert.deepEqual(splitSerials("A-1, a-1\nB-2;C-3\tD-4   \n\n"), ["A-1", "B-2", "C-3", "D-4"]);
});

test("the serial of a scan is the GS1 21 or MH10 S field, else the cleaned text", () => {
  assert.equal(serialFromScan([{ key: "01", value: "09506000134352" }, { key: "21", value: "2506493661" }], "x"), "2506493661");
  assert.equal(serialFromScan([{ key: "S", value: "ABC" }], "x"), "ABC");
  assert.equal(serialFromScan([], "2506-493661"), "2506-493661");
});

test("match spans and time formatting", () => {
  assert.deepEqual(matchSpan("2506-493445", "2506-4934"), { start: 0, length: 9 });
  assert.equal(matchSpan("2506-493445", "zzz"), null);
  assert.equal(fmtDateTime("2026-10-15T09:41:07.123Z"), "2026-10-15 09:41");
});
