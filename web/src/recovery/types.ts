export interface RecoveryClaimRow {
  recoveryClaimId: string;
  recoveryNumber: string;
  raisedAt: string;
  customerClaimId: string;
  customerClaimNumber: string;
  claimDate: string;
  claimApprovedAt: string | null;
  childUnitId: string;
  childSerial: string;
  childItemId: string;
  parentUnitId: string | null;
  parentSerial: string | null;
  vendorPartyRef: string;
  vendorTermStart: string | null;
  vendorTermEnd: string | null;
  vendorTermMonths: number | null;
  claimMonthOfTerm: number | null;
  requestedAmount: number;
  approvedAmount: number | null;
  currency: string;
  status: string;
  ageDays: number;
  approvedAt: string | null;
  approvedBy: string | null;
  vendorReference: string | null;
  vendorNote: string | null;
}

export interface RecoveryClaimPage { rows: RecoveryClaimRow[]; total: number; skip: number; take: number }

export interface RecoveryCurrencyTotal { currency: string; claimCount: number; requestedAmount: number; approvedAmount: number }
export interface RecoveryFilterOption { id: string; count: number }
export interface OldestOpenRecovery { ageDays: number; recoveryNumber: string; vendorPartyRef: string }

export interface RecoveryClaimSummary {
  openCount: number;
  openAmounts: RecoveryCurrencyTotal[];
  approvedThisYear: RecoveryCurrencyTotal[];
  termExpiredWithoutRecovery: number;
  oldestOpen: OldestOpenRecovery | null;
  vendors: RecoveryFilterOption[];
  childItems: RecoveryFilterOption[];
}

export type RecoveryNotRaisedReason = "vendor_term_ended" | "no_vendor_term" | "recall_claim";

export interface RecoveryNotRaisedRow {
  customerClaimId: string;
  customerClaimNumber: string;
  claimDate: string;
  approvedAt: string;
  unitId: string;
  unitSerial: string;
  itemId: string;
  reason: RecoveryNotRaisedReason;
  vendorPartyRef: string | null;
  vendorTermStart: string | null;
  vendorTermEnd: string | null;
  vendorTermMonths: number | null;
  claimMonthOfTerm: number | null;
}

export interface RecoveryNotRaisedReport { days: number; rows: RecoveryNotRaisedRow[] }

export interface RecoveryAnswer {
  commandId: string;
  approvedAmount: number;
  eventTime: string;
  vendorReference: string | null;
  vendorNote: string | null;
}

export interface RecoveryAnswerResult {
  recoveryClaimId: string;
  status: string;
  requestedAmount: number;
  approvedAmount: number | null;
  eventId: string;
  replayed: boolean;
  vendorReference: string | null;
  vendorNote: string | null;
}
