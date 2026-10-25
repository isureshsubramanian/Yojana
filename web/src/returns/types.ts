import * as z from "zod/mini";
export const documentRef = z.object({ type: z.string(), id: z.string(), line: z.nullable(z.number()) });
export type DocumentRef = z.infer<typeof documentRef>;
export const rmaListRow = z.object({ rmaId: z.string(), rmaNumber: z.nullable(z.string()), stage: z.string(),
  requestDate: z.string(), updatedAt: z.string(), ageDays: z.number(), returningPartyRef: z.string(),
  lineCount: z.number(), exceptionCount: z.number(), primaryUnitId: z.nullable(z.string()),
  primarySerial: z.nullable(z.string()), primaryItemId: z.nullable(z.string()), receiptOutcome: z.nullable(z.string()),
  conditionGrade: z.nullable(z.string()), coverageSource: z.nullable(z.string()), receivedAt: z.nullable(z.string()),
  requestedBy: z.string(), lastActorRef: z.nullable(z.string()) });
export type RmaListRow = z.infer<typeof rmaListRow>;
export const rmaStageCount = z.object({ stage: z.string(), count: z.number(), oldestAgeDays: z.nullable(z.number()),
  exceptionCount: z.number() });
export type RmaStageCount = z.infer<typeof rmaStageCount>;
export const rmaListResult = z.object({ rows: z.array(rmaListRow), summary: z.array(rmaStageCount),
  continuation: z.nullable(z.string()), hasMore: z.boolean(), take: z.number() });
export type RmaListResult = z.infer<typeof rmaListResult>;
export const rmaDetailLine = z.object({ rmaLineId: z.string(), sequence: z.number(), requestedUnitId: z.string(),
  authorisedUnitId: z.nullable(z.string()), ownershipEpisodeId: z.string(), originalSaleRef: documentRef,
  parentUnitId: z.nullable(z.string()), priceAllocation: z.nullable(z.number()), parentIncomplete: z.boolean(),
  coveragePct: z.nullable(z.number()), approvedAmount: z.nullable(z.number()), coverageSource: z.nullable(z.string()),
  entitlementId: z.nullable(z.string()), denialReason: z.nullable(z.string()), receivedUnitId: z.nullable(z.string()),
  provisionalUnitId: z.nullable(z.string()), receiptOutcome: z.nullable(z.string()),
  conditionGrade: z.nullable(z.string()), valuationRule: z.nullable(z.string()), disposition: z.nullable(z.string()),
  replacementIntentId: z.nullable(z.string()), entitlementClosedReason: z.nullable(z.string()),
  description: z.nullable(z.string()), requestedItemId: z.nullable(z.string()),
  requestedSerial: z.nullable(z.string()), authorisedSerial: z.nullable(z.string()),
  receivedItemId: z.nullable(z.string()), receivedSerial: z.nullable(z.string()),
  receiptBusinessTime: z.nullable(z.string()), postingPeriod: z.nullable(z.string()),
  receivedBy: z.nullable(z.string()), receivingLocationId: z.nullable(z.string()),
  exceptionNote: z.nullable(z.string()) });
export type RmaDetailLine = z.infer<typeof rmaDetailLine>;
export const rmaDispatchDetail = z.object({ dispatchedAt: z.string(), carrierName: z.nullable(z.string()),
  trackingReference: z.nullable(z.string()), dispatchedBy: z.string() });
export type RmaDispatchDetail = z.infer<typeof rmaDispatchDetail>;
export const rmaOpenFollowUp = z.object({ rmaLineId: z.string(), sequence: z.number(), disposition: z.string(),
  code: z.string(), detail: z.string() });
export type RmaOpenFollowUp = z.infer<typeof rmaOpenFollowUp>;
export const rmaDetail = z.object({ rmaId: z.string(), rmaNumber: z.nullable(z.string()), stage: z.string(),
  requestDate: z.string(), returningPartyRef: z.string(), settlementPartyRef: z.string(), serviceEntityId: z.string(),
  serviceCountry: z.string(), currency: z.string(), requestedAt: z.string(), authorisedAt: z.nullable(z.string()),
  inTransitAt: z.nullable(z.string()), receivedAt: z.nullable(z.string()), dispositionedAt: z.nullable(z.string()),
  closedAt: z.nullable(z.string()), requestedBy: z.string(), lastActorRef: z.nullable(z.string()),
  lines: z.array(rmaDetailLine), dispatch: z.nullable(rmaDispatchDetail), openFollowUps: z.array(rmaOpenFollowUp),
  version: z.string() });
export type RmaDetail = z.infer<typeof rmaDetail>;
export const closeRmaResult = z.object({ rmaId: z.string(), stage: z.string(), closed: z.boolean(),
  openFollowUps: z.array(rmaOpenFollowUp), eventId: z.nullable(z.string()), replayed: z.boolean() });
export type CloseRmaResult = z.infer<typeof closeRmaResult>;
export const warrantyReplacementRow = z.object({ warrantyReplacementId: z.string(), replacementNumber: z.string(),
  sourceKind: z.string(), sourceId: z.string(), sourceNumber: z.string(), originalUnitId: z.string(),
  replacementUnitId: z.string(), originalItemId: z.string(), replacementItemId: z.string(),
  replacementReason: z.string(), state: z.string(), status: z.string(), pricePolicy: z.string(),
  differenceAmount: z.number(), currency: z.string(), returnDue: z.nullable(z.string()),
  nonReturnPriceBasis: z.nullable(z.string()), replacementConditionAllowed: z.string(),
  replacementCondition: z.nullable(z.string()), consentRefurbished: z.boolean(), policyApplied: z.nullable(z.string()),
  computedEnd: z.nullable(z.string()), restockingFeeAmount: z.number(), createdAt: z.string(), createdBy: z.string(),
  shippedAt: z.nullable(z.string()), shippedBy: z.nullable(z.string()), version: z.string() });
export type WarrantyReplacementRow = z.infer<typeof warrantyReplacementRow>;
export const warrantyReplacementLineRow = z.object({ lineId: z.string(), sequence: z.number(), kind: z.string(),
  itemId: z.nullable(z.string()), description: z.string(), quantity: z.number(), unitAmount: z.number(),
  lineAmount: z.number(), currency: z.string(), revenueRule: z.nullable(z.string()) });
export type WarrantyReplacementLineRow = z.infer<typeof warrantyReplacementLineRow>;
export const warrantyReplacementUnitView = z.object({ unitId: z.string(), serial: z.nullable(z.string()),
  itemId: z.string(), conditionGrade: z.string(), lifecycleStatus: z.string(), locationKind: z.nullable(z.string()),
  locationId: z.nullable(z.string()) });
export type WarrantyReplacementUnitView = z.infer<typeof warrantyReplacementUnitView>;
export const warrantyReplacementCheck = z.object({ code: z.string(), label: z.string(), detail: z.string(),
  outcome: z.string(), passed: z.boolean() });
export type WarrantyReplacementCheck = z.infer<typeof warrantyReplacementCheck>;
export const warrantyReplacementTransferRow = z.object({ entitlementId: z.nullable(z.string()),
  coverageFamily: z.string(), entitlementType: z.string(), unitId: z.string(), unitSerial: z.nullable(z.string()),
  startDate: z.nullable(z.string()), endDate: z.nullable(z.string()), effect: z.string(), detail: z.string() });
export type WarrantyReplacementTransferRow = z.infer<typeof warrantyReplacementTransferRow>;
export const warrantyReplacementNonReturnCharge = z.object({ state: z.string(), priceBasis: z.string(),
  amount: z.number(), billedOn: z.nullable(z.string()) });
export type WarrantyReplacementNonReturnCharge = z.infer<typeof warrantyReplacementNonReturnCharge>;
export const warrantyReplacementDetail = z.object({ document: warrantyReplacementRow, sourceEntitlementId: z.string(),
  targetEntitlementId: z.nullable(z.string()), replacementLinkId: z.string(),
  previousReplacementLinkId: z.nullable(z.string()), originalInvoice: z.nullable(documentRef),
  returnReasonCode: z.nullable(z.string()), restockingFeeRule: z.nullable(z.string()),
  stockPostingReference: z.nullable(z.string()), lines: z.array(warrantyReplacementLineRow),
  originalUnit: warrantyReplacementUnitView, replacementUnit: warrantyReplacementUnitView,
  ownerPartyRef: z.nullable(z.string()), customerAssetTag: z.nullable(z.string()), replacementPolicyCode: z.string(),
  chainLength: z.number(), nonReturnCharge: z.nullable(warrantyReplacementNonReturnCharge),
  checks: z.array(warrantyReplacementCheck), transfers: z.array(warrantyReplacementTransferRow) });
export type WarrantyReplacementDetail = z.infer<typeof warrantyReplacementDetail>;

export interface ReplacementCreateDraft {
  sourceKind: "rma" | "claim"; sourceId: string; sourceEntitlementId: string; replacementUnitId: string;
  replacementReason: string; pricePolicy: string; differenceAmount: number; currency: string; serviceCountry: string;
  consumerContract: boolean; consentRefurbished: boolean; replacementPolicyCode: string; returnDue: string;
  nonReturnPriceBasis: string;
}
