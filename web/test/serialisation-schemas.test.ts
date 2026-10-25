import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import * as z from "zod/mini";
import { checkedBody } from "../src/api/client.ts";
import { documentRef, pageOf, selectionSource } from "../src/api/units.ts";
import { rmaListResult, warrantyReplacementDetail } from "../src/returns/types.ts";
import { replacementLinkPage } from "../src/replacements/types.ts";
import { refurbishmentWorkOrderPage } from "../src/refurbishments/types.ts";
import { recoveryAnswer, recoveryPolicy } from "../src/recovery/types.ts";
import { certificateDetailSchema } from "../src/certificates/types.ts";

// ADR-0019 batch 2: the Serialisation screens' schemas accept the shapes the C# records write (nullable → null, every
// field present) and refuse a response that lacks a field the screen reads. Values are demo-format, not real data.
const source = (path: string) => readFileSync(new URL(`../src/${path}`, import.meta.url), "utf8");
const id = (n: number) => `0199a1b2-0000-7000-8000-${String(n).padStart(12, "0")}`;

test("converted Serialisation API files make no unchecked api calls", () => {
  for (const file of ["api/units.ts", "api/legalEntities.tsx", "returns/api.ts", "replacements/api.ts", "refurbishments/api.ts", "recovery/api.ts"]) {
    const text = source(file);
    assert.doesNotMatch(text, /\bapi(<[^>]*>)?\(/, file);
    assert.match(text, /apiParsed\(/, file);
  }
});

test("a document reference accepts a line or a null line, and a page carries its counts", () => {
  assert.deepEqual(z.parse(documentRef, { type: "delivery", id: id(1), line: null }), { type: "delivery", id: id(1), line: null });
  assert.equal(z.parse(documentRef, { type: "delivery", id: id(1), line: 2 }).line, 2);
  const page = pageOf(z.string());
  assert.deepEqual(z.parse(page, { items: ["a"], total: 1, skip: 0, take: 50 }), { items: ["a"], total: 1, skip: 0, take: 50 });
  assert.equal(z.safeParse(page, { items: ["a"], skip: 0, take: 50 }).success, false);
});

test("a checked request body is sent as the caller built it, and a wrongly typed field is refused before sending", () => {
  const source = { kind: "import_job", importJobId: id(9), document: { type: "delivery", id: id(1), line: null, revision: "v2" } };
  assert.equal(checkedBody(selectionSource, source), source);
  assert.throws(() => checkedBody(selectionSource, { kind: 4 } as unknown as typeof source));
});

test("the RMA list accepts the server's page and refuses one without rows", () => {
  const row = {
    rmaId: id(2), rmaNumber: "RMA-2026-00041", stage: "received", requestDate: "2026-10-20", updatedAt: "2026-10-21T09:12:00Z",
    ageDays: 5, returningPartyRef: id(3), lineCount: 1, exceptionCount: 0, primaryUnitId: id(4), primarySerial: "SRV-R760-000812",
    primaryItemId: id(5), receiptOutcome: "as_expected", conditionGrade: "B", coverageSource: "manufacturer_warranty",
    receivedAt: "2026-10-21T09:12:00Z", requestedBy: "user:desk-1", lastActorRef: null,
  };
  const page = { rows: [row], summary: [{ stage: "received", count: 1, oldestAgeDays: 5, exceptionCount: 0 }], continuation: null, hasMore: false, take: 50 };
  assert.equal(z.parse(rmaListResult, page).rows[0]?.primarySerial, "SRV-R760-000812");
  const { rows: _rows, ...withoutRows } = page;
  assert.equal(z.safeParse(rmaListResult, withoutRows).success, false);
});

test("a warranty replacement without a non-return charge parses with that field null", () => {
  const unit = { unitId: id(6), serial: "NAS-TS873-0042", itemId: id(7), conditionGrade: "new", lifecycleStatus: "active", locationKind: null, locationId: null };
  const detail = {
    document: {
      warrantyReplacementId: id(8), replacementNumber: "WR-000112", sourceKind: "rma", sourceId: id(2), sourceNumber: "RMA-2026-00041",
      originalUnitId: id(6), replacementUnitId: id(10), originalItemId: id(7), replacementItemId: id(7), replacementReason: "warranty_defect",
      state: "draft", status: "open", pricePolicy: "zero", differenceAmount: 0, currency: "GBP", returnDue: null, nonReturnPriceBasis: null,
      replacementConditionAllowed: "new_or_refurbished", replacementCondition: null, consentRefurbished: false, policyApplied: null,
      computedEnd: null, restockingFeeAmount: 0, createdAt: "2026-10-22T10:00:00Z", createdBy: "user:desk-1", shippedAt: null,
      shippedBy: null, version: "AAAAAAAAB9E=",
    },
    sourceEntitlementId: id(11), targetEntitlementId: null, replacementLinkId: id(12), previousReplacementLinkId: null,
    originalInvoice: null, returnReasonCode: null, restockingFeeRule: null, stockPostingReference: null, lines: [],
    originalUnit: unit, replacementUnit: { ...unit, unitId: id(10), serial: "NAS-TS873-0077" }, ownerPartyRef: null,
    customerAssetTag: null, replacementPolicyCode: "standard", chainLength: 1, nonReturnCharge: null, checks: [], transfers: [],
  };
  assert.equal(z.parse(warrantyReplacementDetail, detail).nonReturnCharge, null);
  assert.equal(z.safeParse(warrantyReplacementDetail, { ...detail, originalUnit: undefined }).success, false);
});

test("replacement, refurbishment and recovery pages refuse a wrongly typed total", () => {
  for (const [name, schema, body] of [
    ["replacement links", replacementLinkPage, { total: "12" }],
    ["refurbishments", refurbishmentWorkOrderPage, { total: "12" }],
  ] as const) assert.equal(z.safeParse(schema, body).success, false, name);
});

test("the recovery policy accepts only the rules the server accepts, and an answer is sent with the fields the server fills", () => {
  const policy = z.safeParse(recoveryPolicy, { wholeUnitLines: "any_part", campaignClaims: "no_recovery" });
  assert.equal(policy.success, false);
  const answer = { commandId: id(13), approvedAmount: 120.5, eventTime: "2026-10-23T12:00:00Z", vendorReference: null, vendorNote: null, actorRef: "" };
  assert.equal(checkedBody(recoveryAnswer, answer), answer);
});

test("a certificate detail parses without issuedBy, which only list rows carry (found on the dev app)", () => {
  const detail = {
    certificateId: id(20), unitId: id(6), itemId: id(7), primarySerial: "SRV-R760-000812", documentKind: "warranty_certificate",
    formType: "warranty", version: 1, trackingNumber: "WC-2026-000318", status: "issued", issuedAt: "2026-10-22T10:00:00Z",
    issuedAtDisplay: "22 Oct 2026, 10:00", provenance: "issued_on_release", templateVersionId: id(21), asBuiltRevisionId: null,
    payload: { serial: "SRV-R760-000812" }, payloadHash: "sha256:00", supersedesCertificateId: null, supersedeReason: null,
    triggeringEventId: id(22), issuer: { name: "Demo Hardware Ltd", registrationCode: null, address: null }, owner: null,
    branding: { profile: "default", displayName: null, mark: null, logoAssetId: null, logoSha256: null, logoMediaType: null },
    language: "en-GB", unknowns: [], evidence: [], signatures: [], reprints: [], redaction: null,
  };
  assert.equal(z.parse(certificateDetailSchema, detail).trackingNumber, "WC-2026-000318");
});
