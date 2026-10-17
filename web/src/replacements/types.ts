export type AwaitingFilter = "overdue" | "due_soon" | "all";

export interface ReplacementLinkKpis {
  awaiting: number; overdue: number; dueInSevenDays: number; customersOnHold: number;
}

export interface ReplacementLinkRow {
  replacementLinkId: string; warrantyReplacementId: string; replacementNumber: string;
  direction: string; state: string; originalUnitId: string; replacementUnitId: string;
  counterpartyPartyRef: string | null; returnDue: string | null; daysOverdue: number | null;
  nonReturnPriceBasis: string | null; nonReturnAmount: number | null; currency: string;
  overduePublishedAt: string | null; partyOnHold: boolean; originalSerial: string | null;
  replacementSerial: string | null; originalItemId: string; replacementItemId: string;
  sourceNumber: string; replacementReason: string; shippedAt: string | null;
}

export interface ReplacementLinkPage {
  rows: ReplacementLinkRow[]; kpis: ReplacementLinkKpis; total: number; skip: number; take: number;
}

export interface ExtendReturnDueResult {
  replacementLinkId: string; previousReturnDue: string; newReturnDue: string; eventId: string; replayed: boolean;
}

export interface BillNonReturnResult {
  replacementLinkId: string; state: string; priceBasis: string; amount: number; currency: string; eventId: string; replayed: boolean;
}

export interface LiftReplacementHoldResult { holdId: string; partyRef: string; liftedAt: string; replayed: boolean }
