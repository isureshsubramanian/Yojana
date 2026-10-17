export interface RefurbishmentOperationRow {
  operationId: string; sequence: number; description: string; technicianRef: string | null;
  plannedHours: number; actualHours: number | null; status: string; signedBy: string | null; signedAt: string | null;
}
export interface RefurbishmentPartRow {
  partId: string; action: string; childUnitId: string | null; childItemId: string | null; slot: string | null;
  destination: string | null; vendorPartyRef: string | null; assemblyLinkId: string | null;
  unitCost: number | null; currency: string | null; recordedAt: string;
}
export interface RefurbishmentLabourRow {
  labourId: string; operationId: string | null; hours: number; hourlyRate: number; amount: number;
  currency: string; technicianRef: string; recordedAt: string;
}
export interface RefurbishmentCostRow {
  costId: string; kind: string; amount: number; currency: string; description: string; postedAt: string; eventId: string;
}
export interface RefurbishmentReleaseCheck { key: string; label: string; detail: string; passed: boolean; required: boolean }
export interface RefurbishmentWorkOrderRow {
  workOrderId: string; workOrderNumber: string; kind: string; targetUnitId: string; itemId: string;
  sourceRmaLineId: string | null; status: string; cycleNumber: number; identityPolicy: string; maxCycles: number;
  openedAt: string; openedBy: string; releasedAt: string | null; releasedBy: string | null; releasedUnitId: string | null;
  grade: string | null; totalCost: number; currency: string | null; operations: RefurbishmentOperationRow[];
  parts: RefurbishmentPartRow[]; labour: RefurbishmentLabourRow[]; costs: RefurbishmentCostRow[];
  targetSerial: string | null; releasedSerial: string | null; currentLocationId: string | null;
  releaseChecks: RefurbishmentReleaseCheck[];
}
export interface RefurbishmentWorkOrderPage { items: RefurbishmentWorkOrderRow[]; total: number; skip: number; take: number }

export interface RefurbishmentItemClassPolicy { itemClass: string; identity: string; maxCycles: number; replacementPolicyCode: string | null }
export interface RefurbishmentItemCyclePolicy { itemId: string; maxCycles: number }
export interface RefurbishmentPolicy {
  defaultIdentity: string; defaultMaxCycles: number; itemClasses: RefurbishmentItemClassPolicy[];
  defaultReplacementPolicyCode: string | null; items: RefurbishmentItemCyclePolicy[] | null;
}

export interface SanitisationRow {
  sanitisationRecordId: string; manufacturer: string; model: string; mediaSerial: string; mediaType: string;
  method: string; tool: string; toolVersion: string | null; verificationMethod: string; operatorRef: string;
  sanitisedAt: string; certificateRef: string; signatureRef: string | null; eventId: string;
}
export interface UnitTestRow { testRecordId: string; testKind: string; result: string; measurementsJson: string; equipment: string; operatorRef: string; testedAt: string; eventId: string }
export interface UsageReadingRow { usageReadingId: string; meterKind: string; value: number; unit: string; readAt: string; meterReplacement: boolean; operatorRef: string; eventId: string }
export interface UnitAttributeHistoryRow { attributeHistoryId: string; key: string; value: string | null; effectiveFrom: string; actorRef: string; eventId: string }

export type RefurbishmentDialog = "add_operation" | "remove_part" | "install_part" | "labour" | "cost" | "release" | "cancel" | null;
