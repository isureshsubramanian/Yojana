import * as z from "zod/mini";
export type AwaitingFilter = "overdue" | "due_soon" | "all";

export const replacementLinkKpis = z.object({ awaiting: z.number(), overdue: z.number(), dueInSevenDays: z.number(),
  customersOnHold: z.number() });
export type ReplacementLinkKpis = z.infer<typeof replacementLinkKpis>;
export const replacementLinkRow = z.object({ replacementLinkId: z.string(), warrantyReplacementId: z.string(),
  replacementNumber: z.string(), direction: z.string(), state: z.string(), originalUnitId: z.string(),
  replacementUnitId: z.string(), counterpartyPartyRef: z.nullable(z.string()), returnDue: z.nullable(z.string()),
  daysOverdue: z.nullable(z.number()), nonReturnPriceBasis: z.nullable(z.string()),
  nonReturnAmount: z.nullable(z.number()), currency: z.string(), overduePublishedAt: z.nullable(z.string()),
  partyOnHold: z.boolean(), originalSerial: z.nullable(z.string()), replacementSerial: z.nullable(z.string()),
  originalItemId: z.string(), replacementItemId: z.string(), sourceNumber: z.string(), replacementReason: z.string(),
  shippedAt: z.nullable(z.string()) });
export type ReplacementLinkRow = z.infer<typeof replacementLinkRow>;
export const replacementLinkPage = z.object({ rows: z.array(replacementLinkRow), kpis: replacementLinkKpis,
  total: z.number(), skip: z.number(), take: z.number() });
export type ReplacementLinkPage = z.infer<typeof replacementLinkPage>;
export const extendReturnDueResult = z.object({ replacementLinkId: z.string(), previousReturnDue: z.string(),
  newReturnDue: z.string(), eventId: z.string(), replayed: z.boolean() });
export type ExtendReturnDueResult = z.infer<typeof extendReturnDueResult>;
export const billNonReturnResult = z.object({ replacementLinkId: z.string(), state: z.string(), priceBasis: z.string(),
  amount: z.number(), currency: z.string(), eventId: z.string(), replayed: z.boolean() });
export type BillNonReturnResult = z.infer<typeof billNonReturnResult>;
export const liftReplacementHoldResult = z.object({ holdId: z.string(), partyRef: z.string(), liftedAt: z.string(),
  replayed: z.boolean() });
export type LiftReplacementHoldResult = z.infer<typeof liftReplacementHoldResult>;

