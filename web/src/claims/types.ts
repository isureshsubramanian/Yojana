export interface ClaimListRow {
  claimId: string; claimNumber: string; unitId: string; serial: string | null; itemId: string | null;
  claimantPartyRef: string; settlementPartyRef: string; status: string; coverageSource: string; remedy: string;
  serviceCountry: string; currency: string; claimDate: string; submittedAt: string; requestedAmount: number;
  approvedAmount: number; ageDays: number; denialReason: string | null; recoveryRaised: boolean;
  evidenceRequested: boolean; lineCount: number; linesDecided: number; recoveryVendorNote: string | null;
}

export interface ClaimPage { items: ClaimListRow[]; total: number; skip: number; take: number }
export interface MoneyTotal { currency: string; amount: number }
export interface ReasonCount { reason: string; count: number }
export interface ClaimSummary {
  asOf: string; open: number; submitted: number; adjudicating: number; approvedNotSettled: number; deniedNotSettled: number;
  approvedThisMonth: MoneyTotal[]; approvedCountThisMonth: number; partialCountThisMonth: number;
  deniedCountThisMonth: number; deniedReasonsThisMonth: ReasonCount[]; recoveryOpen: MoneyTotal[];
  recoveryOpenCount: number; recoveryVendorCount: number; oldestOpenNumber: string | null; oldestOpenClaimId: string | null;
  oldestOpenAgeDays: number | null; oldestOpenStatus: string | null; oldestOpenEvidenceRequested: boolean;
  ageBuckets: number[]; adjudicationTargetWorkingDays: number; coverageSources: string[]; serviceCountries: string[]; allClaims: number;
}

export interface ClaimLineRow {
  claimLineId: string; sequence: number; unitId: string; serial: string | null; itemId: string | null; kind: string;
  coverageFamily: string; description: string | null; requestedAmount: number; coveragePct: number; approvedAmount: number;
  coverageSource: string; denialReason: string | null; measurements: Record<string, number>;
}
export interface ClaimLineDecision {
  claimLineId: string; unitId: string; coverageFamily: string; requestedAmount: number; coveragePct: number;
  approvedAmount: number; coverageSource: string; entitlementId: string | null; denialReason: string | null; chainLength: number;
}
export interface ClaimEvidenceRow { evidenceId: string; kind: string; reference: string; description: string | null; recordedAt: string; actorRef: string }
export interface ClaimRecoveryRef { recoveryClaimId: string; recoveryNumber: string; vendorPartyRef: string; status: string }
export interface ClaimDetail {
  claimId: string; unitId: string; status: string; recoveryRaised: boolean; claimDate: string; claimantPartyRef: string;
  settlementPartyRef: string; serviceEntityId: string; obligorEntityId: string; serviceCountry: string; currency: string;
  coverageSource: string; remedy: string; denialReason: string | null; chainLength: number; entitlementIds: string[];
  lines: ClaimLineDecision[]; evidence: ClaimEvidenceRow[]; adjudicatedBy: string | null; reviewerRole: string | null;
  successorItemId: string | null; claimNumber: string; serial: string | null; itemId: string | null; submittedAt: string;
  submittedBy: string; adjudicatedAt: string | null; lineRows: ClaimLineRow[] | null; recoveries: ClaimRecoveryRef[] | null; openWork: string | null;
}

export interface PolicyStackStep { level: string; outcome: string; detail: string }
export interface ClaimLinePreview {
  claimLineId: string; sequence: number; unitId: string; serial: string | null; itemId: string | null; isChild: boolean;
  kind: string; coverageFamily: string; description: string | null; requestedAmount: number; coveragePct: number;
  approvedAmount: number; coverageSource: string; level: string; policyCode: string | null; policyVersion: number | null;
  entitlementId: string | null; coverStart: string | null; coverEnd: string | null; denialReason: string | null; stack: PolicyStackStep[];
}
export interface ClaimCheck { key: string; value: string; outcome: string }
export interface RecoveryPreview {
  childUnitId: string; childSerial: string | null; vendorPartyRef: string; requestedAmount: number;
  termStart: string | null; termEnd: string | null; termMonths: number | null; monthOfTerm: number | null;
}
export interface ClaimAdjudicationPreview {
  claimId: string; claimNumber: string; outcome: string; coverageSource: string; currency: string; requestedAmount: number;
  approvedAmount: number; lines: ClaimLinePreview[]; checks: ClaimCheck[]; cohortId: string | null; obligorEntityId: string;
  recoveries: RecoveryPreview[]; chargeableAmount: number; refusal: string | null; refusalStep: string | null;
  requiredReviewerRole: string | null; successorItemId: string | null; manualDenialReasons: string[];
}

export interface ClaimLineInput {
  kind: string; coverageFamily: string; requestedAmount: number; unitId: string | null; description: string | null;
  measurements: Record<string, number> | null;
}
export interface ClaimEvidenceInput { kind: string; reference: string; description: string | null }
export interface SubmitClaim {
  commandId: string; unitId: string | null; identifier: { value: string; issuerPartyRef: string | null; identifierType: string | null; erpSerialId: string | null } | null;
  claimDate: string; claimantPartyRef: string; settlementPartyRef: string; serviceEntityId: string; obligorEntityId: string;
  serviceCountry: string; currency: string; lines: ClaimLineInput[]; evidence: ClaimEvidenceInput[]; remedy: string;
  consumerChoiceEvidenceRef: string; eventTime: string; actorRef: string; proofOfPurchaseRef: string | null;
  proofOfPurchaseDate: string | null; itemId: string | null;
}
export interface SubmitClaimResult { claimId: string; unitId: string; status: string; claimLineIds: string[]; eventId: string; replayed: boolean }
export interface ClaimChangeResult { claimId: string; status: string; eventId: string; replayed: boolean }

export interface TenantCurrency { currency: string | null; accepted: string[] }
export interface CurrencyInfo { code: string; name: string; symbol: string; minorDigits: number }
export interface CoverageFamily { familyCode: string; name: string; stacking: boolean; accountingTag: string; active: boolean }

