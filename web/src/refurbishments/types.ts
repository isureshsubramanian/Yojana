import * as z from "zod/mini";
export const refurbishmentOperationRow = z.object({ operationId: z.string(), sequence: z.number(),
  description: z.string(), technicianRef: z.nullable(z.string()), plannedHours: z.number(),
  actualHours: z.nullable(z.number()), status: z.string(), signedBy: z.nullable(z.string()),
  signedAt: z.nullable(z.string()) });
export type RefurbishmentOperationRow = z.infer<typeof refurbishmentOperationRow>;
export const refurbishmentPartRow = z.object({ partId: z.string(), action: z.string(),
  childUnitId: z.nullable(z.string()), childItemId: z.nullable(z.string()), slot: z.nullable(z.string()),
  destination: z.nullable(z.string()), vendorPartyRef: z.nullable(z.string()), assemblyLinkId: z.nullable(z.string()),
  unitCost: z.nullable(z.number()), currency: z.nullable(z.string()), recordedAt: z.string() });
export type RefurbishmentPartRow = z.infer<typeof refurbishmentPartRow>;
export const refurbishmentLabourRow = z.object({ labourId: z.string(), operationId: z.nullable(z.string()),
  hours: z.number(), hourlyRate: z.number(), amount: z.number(), currency: z.string(), technicianRef: z.string(),
  recordedAt: z.string() });
export type RefurbishmentLabourRow = z.infer<typeof refurbishmentLabourRow>;
export const refurbishmentCostRow = z.object({ costId: z.string(), kind: z.string(), amount: z.number(),
  currency: z.string(), description: z.string(), postedAt: z.string(), eventId: z.string() });
export type RefurbishmentCostRow = z.infer<typeof refurbishmentCostRow>;
export const refurbishmentReleaseCheck = z.object({ key: z.string(), label: z.string(), detail: z.string(),
  passed: z.boolean(), required: z.boolean() });
export type RefurbishmentReleaseCheck = z.infer<typeof refurbishmentReleaseCheck>;
export const refurbishmentWorkOrderRow = z.object({ workOrderId: z.string(), workOrderNumber: z.string(),
  kind: z.string(), targetUnitId: z.string(), itemId: z.string(), sourceRmaLineId: z.nullable(z.string()),
  status: z.string(), cycleNumber: z.number(), identityPolicy: z.string(), maxCycles: z.number(), openedAt: z.string(),
  openedBy: z.string(), releasedAt: z.nullable(z.string()), releasedBy: z.nullable(z.string()),
  releasedUnitId: z.nullable(z.string()), grade: z.nullable(z.string()), totalCost: z.number(),
  currency: z.nullable(z.string()), operations: z.array(refurbishmentOperationRow),
  parts: z.array(refurbishmentPartRow), labour: z.array(refurbishmentLabourRow), costs: z.array(refurbishmentCostRow),
  targetSerial: z.nullable(z.string()), releasedSerial: z.nullable(z.string()),
  currentLocationId: z.nullable(z.string()), releaseChecks: z.array(refurbishmentReleaseCheck), version: z.string() });
export type RefurbishmentWorkOrderRow = z.infer<typeof refurbishmentWorkOrderRow>;
export const refurbishmentWorkOrderPage = z.object({ items: z.array(refurbishmentWorkOrderRow), total: z.number(),
  skip: z.number(), take: z.number() });
export type RefurbishmentWorkOrderPage = z.infer<typeof refurbishmentWorkOrderPage>;
export const refurbishmentItemClassPolicy = z.object({ itemClass: z.string(), identity: z.string(),
  maxCycles: z.number(), replacementPolicyCode: z.nullable(z.string()) });
export type RefurbishmentItemClassPolicy = z.infer<typeof refurbishmentItemClassPolicy>;
export const refurbishmentItemCyclePolicy = z.object({ itemId: z.string(), maxCycles: z.number() });
export type RefurbishmentItemCyclePolicy = z.infer<typeof refurbishmentItemCyclePolicy>;
export const refurbishmentPolicy = z.object({ defaultIdentity: z.string(), defaultMaxCycles: z.number(),
  itemClasses: z.array(refurbishmentItemClassPolicy), defaultReplacementPolicyCode: z.nullable(z.string()),
  items: z.nullable(z.array(refurbishmentItemCyclePolicy)) });
export type RefurbishmentPolicy = z.infer<typeof refurbishmentPolicy>;
export const sanitisationRow = z.object({ sanitisationRecordId: z.string(), manufacturer: z.string(),
  model: z.string(), mediaSerial: z.string(), mediaType: z.string(), method: z.string(), tool: z.string(),
  toolVersion: z.nullable(z.string()), verificationMethod: z.string(), operatorRef: z.string(),
  sanitisedAt: z.string(), certificateRef: z.string(), signatureRef: z.nullable(z.string()), eventId: z.string() });
export type SanitisationRow = z.infer<typeof sanitisationRow>;
export const unitTestRow = z.object({ testRecordId: z.string(), testKind: z.string(), result: z.string(),
  measurementsJson: z.string(), equipment: z.string(), operatorRef: z.string(), testedAt: z.string(),
  eventId: z.string() });
export type UnitTestRow = z.infer<typeof unitTestRow>;
export const usageReadingRow = z.object({ usageReadingId: z.string(), meterKind: z.string(), value: z.number(),
  unit: z.string(), readAt: z.string(), meterReplacement: z.boolean(), operatorRef: z.string(), eventId: z.string() });
export type UsageReadingRow = z.infer<typeof usageReadingRow>;
export const unitAttributeHistoryRow = z.object({ attributeHistoryId: z.string(), key: z.string(),
  value: z.nullable(z.string()), effectiveFrom: z.string(), actorRef: z.string(), eventId: z.string() });
export type UnitAttributeHistoryRow = z.infer<typeof unitAttributeHistoryRow>;

export type RefurbishmentDialog = "add_operation" | "remove_part" | "install_part" | "labour" | "cost" | "release" | "cancel" | null;
