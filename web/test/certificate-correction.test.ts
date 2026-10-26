import assert from "node:assert/strict";
import test from "node:test";
import { changeLabel, correctState, dispositionFileName, factChanges, mayIssueCertificates, printCopyState, reasonProblem } from "../src/certificates/correction.ts";
import { certificateCorrectionPreviewSchema } from "../src/certificates/types.ts";
import * as z from "zod/mini";

const record = { certificateId: "c1", documentKind: "release_record", status: "issued", version: 1 };
const row = (version: number) => ({ certificateId: `c${version}`, unitId: "u", itemId: null, primarySerial: "S", documentKind: "release_record",
  formType: "generic_release", version, trackingNumber: `CRT-${version}`, status: version === 1 ? "superseded" : "issued", issuedAt: "", issuedAtDisplay: "", provenance: "p" });

test("only the quality and certificate administrator roles may issue; the tenant administrator may not", () => {
  assert.equal(mayIssueCertificates(["quality"]), true);
  assert.equal(mayIssueCertificates(["certificate_administrator"]), true);
  assert.equal(mayIssueCertificates(["tenant_administrator"]), false);
});

test("print copy is offered to issuers and explains the role to everyone else", () => {
  assert.equal(printCopyState(["quality"]).enabled, true);
  const blocked = printCopyState(["tenant_administrator"]);
  assert.equal(blocked.enabled, false);
  assert.match(blocked.tip, /Quality or Certificate administrator role/);
});

test("correction is offered on the current issued release record only", () => {
  assert.equal(correctState(record, [row(1)], ["quality"])?.enabled, true);
  assert.equal(correctState({ ...record, documentKind: "warranty_certificate" }, [], ["quality"]), null);
  const older = correctState({ ...record, status: "superseded" }, [row(1), row(2)], ["quality"]);
  assert.equal(older?.enabled, false);
  assert.match(older?.tip ?? "", /current version/);
  assert.match(correctState(record, [row(1)], ["tenant_administrator"])?.tip ?? "", /Quality or Certificate administrator/);
});

test("a correction reason needs 10 to 500 characters after trimming", () => {
  assert.notEqual(reasonProblem("   short   "), null);
  assert.equal(reasonProblem("Part names corrected: ERP item descriptions"), null);
  assert.notEqual(reasonProblem("x".repeat(501)), null);
});

test("grouped rows read as one change and derived summaries are not counted as facts", () => {
  const preview = z.parse(certificateCorrectionPreviewSchema, {
    certificateId: "c1", trackingNumber: "CRT-2026-000331", fromVersion: 1, toVersion: 2, canCorrect: true, refusalCode: null, refusalDetail: null,
    changes: [
      { path: "model.name", label: "Model", before: "ITEM-8C647191", after: "Kestrel Systems 2U storage server", rows: 1, derived: false },
      { path: "as_built.item_name", label: "Part", before: "ITEM-97156C8D", after: "Quill 16 TB hard drive · YD-HDD", rows: 8, derived: false },
      { path: "installed_parts", label: "Installed parts", before: null, after: "8 parts", rows: 1, derived: true },
    ],
  });
  assert.equal(changeLabel(preview.changes[1]!), "Part, 8 rows");
  assert.deepEqual(factChanges(preview.changes).map((change) => change.label), ["Model", "Part"]);
});

test("the downloaded copy keeps the server's file name", () => {
  assert.equal(dispositionFileName("attachment; filename=\"YJ2U-CRT-1-v1-copy.pdf\"", "x"), "YJ2U-CRT-1-v1-copy.pdf");
  assert.equal(dispositionFileName("attachment; filename=a.pdf; filename*=UTF-8''YJ2U%20copy.html", "x"), "YJ2U copy.html");
  assert.equal(dispositionFileName(null, "fallback.pdf"), "fallback.pdf");
});
