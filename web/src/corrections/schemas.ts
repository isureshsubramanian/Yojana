import * as z from "zod/mini";
import { asBuiltRow, buildRecordRow, documentRef, documentRefRequest, episodeRow, historyRow, identifierRow, selectionSource, unitDetail } from "../api/units.ts";

// Correction-screen wire contracts. Unit, identifier and document shapes come from api/units.ts (one copy of each
// Serialisation record); the preview and refusal shapes below exist only on correction endpoints.
const text = z.string();
const number = z.number();
const nullableText = z.nullable(text);
const nullableNumber = z.nullable(number);

export { asBuiltRow, buildRecordRow, documentRef, documentRefRequest, episodeRow, historyRow, identifierRow, unitDetail };
export const documentRefInput = documentRefRequest;
export const selectionSourceInput = selectionSource;
export const selectionSourceRequest = selectionSource;

export const commandRefusal = z.object({ rule: text, message: text, unblockingStep: nullableText });
export const unitOutcome = z.object({
  unitId: text, serial: nullableText, outcome: text, reason: nullableText, detail: nullableText, unblockingStep: nullableText,
});
export const correctionEventPreview = z.object({ unitId: text, eventType: text, effectiveAt: text, summary: nullableText });
export const correctionEntitlementPreview = z.object({ unitId: text, change: text, entitlementId: nullableText, summary: nullableText });
export const correctionCertificatePreview = z.object({ unitId: text, certificateType: text, action: text, reason: text });
export const correctionPrintJobPreview = z.object({ unitId: text, jobType: text, reason: text });
export const correctionAllocationPreview = z.object({
  pair: number,
  fromSerial: text,
  exchangeSerial: nullableText,
  fromSerialRangeId: text,
  exchangeSerialRangeId: nullableText,
  fromProductionOrder: documentRef,
  toProductionOrder: documentRef,
  outcome: text,
  rule: nullableText,
  detail: nullableText,
  unblockingStep: nullableText,
});
export const correctionCommissionPreview = z.object({
  serial: text, locationId: nullableText, locationKind: nullableText, sourceDocument: documentRef, forKit: z.boolean(),
});
export const correctionExemptionPreview = z.object({ locationId: nullableText, locationKind: text, quantity: number, reason: text });
export const correctionAsBuiltRequirementPreview = z.object({
  parentUnitId: text, assemblyLinkId: text, quantity: number, capturedByCommand: z.boolean(),
});
export const correctionPreview = z.object({
  selectionId: nullableText,
  command: text,
  unitCount: number,
  change: number,
  skip: number,
  block: number,
  units: z.array(unitOutcome),
  unitEvents: z.array(correctionEventPreview),
  entitlementChanges: z.array(correctionEntitlementPreview),
  certificateVersions: z.array(correctionCertificatePreview),
  printJobs: z.array(correctionPrintJobPreview),
  refusal: z.nullable(commandRefusal),
  allocationReassignments: z.nullable(z.array(correctionAllocationPreview)),
  unitsToCommission: z.nullable(z.array(correctionCommissionPreview)),
  exemptions: z.nullable(z.array(correctionExemptionPreview)),
  kitsNeedingAsBuiltCapture: z.nullable(z.array(correctionAsBuiltRequirementPreview)),
});
