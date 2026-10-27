import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import { activationReasons, bindingTableRows, highlightLayout, reviewActions } from "../src/labels/editorModel.ts";
import { dayBounds, defaultPrintJobDays, printJobRejectionSummary, summaryKpis } from "../src/labels/printJobModel.ts";
import { labelBindableFieldSchema } from "../src/labels/types.ts";
import { sheetFit, sheetName } from "../src/labels/sheetModel.ts";

test("label layout mirror marks placeholders and every legal phrase hit without changing text", () => {
  const layout = "^FD{{serial}} Warranty void if removed. void^FS";
  const segments = highlightLayout(layout, ["void", "warranty void"]);
  assert.equal(segments.map((segment) => segment.text).join(""), layout);
  assert.deepEqual(segments.filter((segment) => segment.kind === "placeholder").map((segment) => segment.text), ["{{serial}}"]);
  assert.deepEqual(segments.filter((segment) => segment.kind === "phrase").map((segment) => segment.text), ["Warranty void", "void"]);
});

test("labels API schemas accept the DTO and refuse a missing required field", () => {
  assert.equal(labelBindableFieldSchema.safeParse({ fieldPath: "identifier.internal_serial", expiryControlled: false }).success, true);
  assert.equal(labelBindableFieldSchema.safeParse({ fieldPath: "identifier.internal_serial" }).success, false);
  const source = readFileSync(new URL("../src/labels/api.ts", import.meta.url), "utf8");
  assert.doesNotMatch(source, /\bapi(?:<[^>]*>)?\(/);
});

test("legal phrases match whole words or phrase boundaries, case-insensitively", () => {
  assert.equal(highlightLayout("avoid devoid", ["void"]).some((segment) => segment.kind === "phrase"), false);
  assert.deepEqual(highlightLayout("Warranty void.", ["void"]).filter((segment) => segment.kind === "phrase").map((segment) => segment.text), ["void"]);
});

test("binding table keeps required flags and annotates expiry-controlled catalogue fields", () => {
  const rows = bindingTableRows(
    [{ placeholder: "serial", fieldPath: "identifier.internal_serial", required: true }, { placeholder: "expiry", fieldPath: "entitlement.base.end_date", required: false }],
    [{ fieldPath: "identifier.internal_serial", expiryControlled: false }, { fieldPath: "entitlement.base.end_date", expiryControlled: true }],
    [{ placeholder: "serial", fieldPath: "identifier.internal_serial", required: true, value: "SN-100" }],
  );
  assert.equal(rows[0]?.required, true);
  assert.equal(rows[0]?.sampleValue, "SN-100");
  assert.equal(rows[1]?.required, false);
  assert.equal(rows[1]?.expiryControlled, true);
  assert.equal(rows[1]?.sampleValue, null);
});

test("legal-review controls require the role and never let the draft author sign", () => {
  const version = { status: "draft" as const, legalReview: true, legalReviewedAt: null, regulated: true, approvedAt: null, actorRef: "author" };
  assert.equal(reviewActions(["label_administrator"], "administrator", version).canRequest, true);
  assert.equal(reviewActions(["label_administrator"], "administrator", version).canSign, false);
  assert.equal(reviewActions(["legal_reviewer"], "reviewer", version).canSign, true);
  assert.equal(reviewActions(["legal_reviewer"], "reviewer", version).canApprove, true);
  const author = reviewActions(["legal_reviewer"], "author", version);
  assert.equal(author.canSign, false);
  assert.equal(author.canApprove, false);
  assert.equal(author.authorConflict, true);
});

test("activation stays disabled until the saved content has current preview, review and approval", () => {
  const ready = { previewCurrent: true, legalReview: true, legalReviewCurrent: true, regulated: true, approvalCurrent: true };
  assert.deepEqual(activationReasons(ready, false), []);
  assert.deepEqual(activationReasons({ ...ready, previewCurrent: false }, false), ["Preview the current saved draft before activation."]);
  assert.deepEqual(activationReasons({ ...ready, legalReviewCurrent: false, approvalCurrent: false }, false), [
    "Legal review is required for the current content.",
    "Regulated-label approval is required for the current content.",
  ]);
  assert.deepEqual(activationReasons(ready, true), ["Save the current draft before activation."]);
});

test("print-job KPIs use the complete server aggregate", () => {
  const kpis = summaryKpis({ printed: 412, confirmed: 98, downloaded: 300, waiting: 12, rejected: 2, reprints: 7, fromEvents: 31, from: "2026-10-18T00:00:00Z", to: "2026-10-19T00:00:00Z" });
  assert.deepEqual(kpis.map((kpi) => kpi.value), ["412", "12", "2", "7", "31"]);
  assert.match(kpis[0]?.detail ?? "", /300/);
});

test("print-job date filters send an exclusive day boundary", () => {
  const bounds = dayBounds("2026-10-18", "2026-10-19");
  assert.equal(new Date(bounds.to).getTime() - new Date(bounds.from).getTime(), 48 * 60 * 60 * 1000);
});

test("print jobs open on an inclusive 30-day local calendar range", () => {
  assert.deepEqual(defaultPrintJobDays(new Date(2026, 9, 19, 16, 30)), { fromDay: "2026-09-20", throughDay: "2026-10-19" });
});

test("print job lists explain missing fields without exposing the stored technical reason", () => {
  assert.equal(printJobRejectionSummary("item.revision", "item.revision is empty for unit 0199"), "Required field Revision is empty");
  assert.equal(printJobRejectionSummary(null, "identifier.manufacturer_serial is empty for unit 0199"), "Required field Manufacturer serial is empty");
  assert.equal(printJobRejectionSummary(null, "printer refused raw payload"), "The label could not be rendered");
});

test("sticker-sheet maths reports an oversized margin as overflow and the default A4 3 × 8 sheet fits", () => {
  const estimated = { profileId: "tenant-default", name: "", pageSize: "A4" as const, columns: 3, rows: 8, labelWidthMm: 70, labelHeightMm: 37, marginTopMm: 4.5, marginRightMm: 0, marginBottomMm: 4.5, marginLeftMm: 0, horizontalGapMm: 0, verticalGapMm: 0, builtIn: false };
  assert.deepEqual(sheetFit(estimated), { fits: false, horizontalOverflowMm: 0, verticalOverflowMm: 8 });
  assert.equal(sheetFit({ ...estimated, marginTopMm: 0.5, marginBottomMm: 0.5 }).fits, true);
  assert.equal(sheetName(estimated), "A4 · 3 × 8 · 70 × 37 mm");
});

test("label screens expose physical sizing, sheet selection, PDF wording and the required reprint reason", () => {
  const editor = readFileSync(new URL("../src/labels/LabelTemplateEditor.tsx", import.meta.url), "utf8");
  const jobs = readFileSync(new URL("../src/labels/PrintJobs.tsx", import.meta.url), "utf8");
  const drawer = readFileSync(new URL("../src/labels/PrintLabelDrawer.tsx", import.meta.url), "utf8");
  const settings = readFileSync(new URL("../src/settings/LabelSettings.tsx", import.meta.url), "utf8");
  assert.match(editor, /Width · mm/); assert.match(editor, /true-scale/);
  assert.match(jobs, /Download PDF/); assert.match(jobs, /printing is not confirmed/i); assert.match(jobs, /Reprint reason/);
  assert.match(drawer, /Start row/); assert.match(drawer, /Start column/); assert.match(drawer, /Download PDF/);
  assert.match(settings, /Sticker sheet/); assert.match(settings, /Grid does not fit/); assert.match(settings, /PDF download/);
});
