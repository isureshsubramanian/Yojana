export interface DocumentRef { type: string; id: string; line: number | null }

export interface RmaListRow {
  rmaId: string; rmaNumber: string | null; stage: string; requestDate: string; updatedAt: string; ageDays: number;
  returningPartyRef: string; lineCount: number; exceptionCount: number; primaryUnitId: string | null; primarySerial: string | null;
  primaryItemId: string | null; receiptOutcome: string | null; conditionGrade: string | null; coverageSource: string | null;
  receivedAt: string | null; requestedBy: string; lastActorRef: string | null;
}
export interface RmaStageCount { stage: string; count: number; oldestAgeDays: number | null; exceptionCount: number }
export interface RmaListResult { rows: RmaListRow[]; summary: RmaStageCount[]; total: number; skip: number; take: number }

export interface RmaDetailLine {
  rmaLineId: string; sequence: number; requestedUnitId: string; authorisedUnitId: string | null; ownershipEpisodeId: string;
  originalSaleRef: DocumentRef; parentUnitId: string | null; priceAllocation: number | null; parentIncomplete: boolean;
  coveragePct: number | null; approvedAmount: number | null; coverageSource: string | null; entitlementId: string | null;
  denialReason: string | null; receivedUnitId: string | null; provisionalUnitId: string | null; receiptOutcome: string | null;
  conditionGrade: string | null; valuationRule: string | null; disposition: string | null; replacementIntentId: string | null;
  entitlementClosedReason: string | null; description: string | null; requestedItemId: string | null; requestedSerial: string | null;
  authorisedSerial: string | null; receivedItemId: string | null; receivedSerial: string | null; receiptBusinessTime: string | null;
  postingPeriod: string | null; receivedBy: string | null; receivingLocationId: string | null; exceptionNote: string | null;
}
export interface RmaDetail {
  rmaId: string; rmaNumber: string | null; stage: string; requestDate: string; returningPartyRef: string; settlementPartyRef: string;
  serviceEntityId: string; serviceCountry: string; currency: string; requestedAt: string; authorisedAt: string | null;
  inTransitAt: string | null; receivedAt: string | null; dispositionedAt: string | null; closedAt: string | null;
  requestedBy: string; lastActorRef: string | null; lines: RmaDetailLine[]; dispatch: RmaDispatchDetail | null;
  openFollowUps: RmaOpenFollowUp[]; version: string;
}
export interface RmaDispatchDetail { dispatchedAt: string; carrierName: string | null; trackingReference: string | null; dispatchedBy: string }
export interface RmaOpenFollowUp { rmaLineId: string; sequence: number; disposition: string; code: string; detail: string }
export interface CloseRmaResult { rmaId: string; stage: string; closed: boolean; openFollowUps: RmaOpenFollowUp[]; eventId: string | null; replayed: boolean }

export interface WarrantyReplacementRow {
  warrantyReplacementId: string; replacementNumber: string; sourceKind: string; sourceId: string; sourceNumber: string;
  originalUnitId: string; replacementUnitId: string; originalItemId: string; replacementItemId: string; replacementReason: string;
  state: string; status: string; pricePolicy: string; differenceAmount: number; currency: string; returnDue: string | null;
  nonReturnPriceBasis: string | null; replacementConditionAllowed: string; replacementCondition: string | null;
  consentRefurbished: boolean; policyApplied: string | null; computedEnd: string | null; restockingFeeAmount: number;
  createdAt: string; createdBy: string; shippedAt: string | null; shippedBy: string | null; version: string;
}
export interface WarrantyReplacementLineRow { lineId: string; sequence: number; kind: string; itemId: string | null; description: string; quantity: number; unitAmount: number; lineAmount: number; currency: string; revenueRule: string | null }
export interface WarrantyReplacementUnitView { unitId: string; serial: string | null; itemId: string; conditionGrade: string; lifecycleStatus: string; locationKind: string | null; locationId: string | null }
export interface WarrantyReplacementCheck { code: string; label: string; detail: string; outcome: string; passed: boolean }
export interface WarrantyReplacementTransferRow { entitlementId: string | null; coverageFamily: string; entitlementType: string; unitId: string; unitSerial: string | null; startDate: string | null; endDate: string | null; effect: string; detail: string }
export interface WarrantyReplacementNonReturnCharge { state: string; priceBasis: string; amount: number; billedOn: string | null }
export interface WarrantyReplacementDetail {
  document: WarrantyReplacementRow; sourceEntitlementId: string; targetEntitlementId: string | null; replacementLinkId: string;
  previousReplacementLinkId: string | null; originalInvoice: DocumentRef | null; returnReasonCode: string | null;
  restockingFeeRule: string | null; stockPostingReference: string | null; lines: WarrantyReplacementLineRow[];
  originalUnit: WarrantyReplacementUnitView; replacementUnit: WarrantyReplacementUnitView; ownerPartyRef: string | null;
  customerAssetTag: string | null; replacementPolicyCode: string; chainLength: number; nonReturnCharge: WarrantyReplacementNonReturnCharge | null; checks: WarrantyReplacementCheck[];
  transfers: WarrantyReplacementTransferRow[];
}

export interface ReplacementCreateDraft {
  sourceKind: "rma" | "claim"; sourceId: string; sourceEntitlementId: string; replacementUnitId: string;
  replacementReason: string; pricePolicy: string; differenceAmount: number; currency: string; serviceCountry: string;
  consumerContract: boolean; consentRefurbished: boolean; replacementPolicyCode: string; returnDue: string;
  nonReturnPriceBasis: string;
}
