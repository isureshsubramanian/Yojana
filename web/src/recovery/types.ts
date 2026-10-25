import * as z from "zod/mini";
export const recoveryCampaignClaimsRule = z.enum(["no_recovery", "recover_like_warranty"]);
export type RecoveryCampaignClaimsRule = z.infer<typeof recoveryCampaignClaimsRule>;
export const recoveryNotRaisedReason = z.enum(["vendor_term_ended", "no_vendor_term", "recall_claim", "campaign_claim", "several_covered_parts", "part_not_named", "line_kind_not_recovered"]);
export type RecoveryNotRaisedReason = z.infer<typeof recoveryNotRaisedReason>;
export const recoveryWholeUnitRule = z.enum(["single_covered_part", "named_part_only"]);
export type RecoveryWholeUnitRule = z.infer<typeof recoveryWholeUnitRule>;
export const recoveryClaimRow = z.object({ recoveryClaimId: z.string(), recoveryNumber: z.string(),
  raisedAt: z.string(), customerClaimId: z.string(), customerClaimNumber: z.string(), claimDate: z.string(),
  claimApprovedAt: z.nullable(z.string()), childUnitId: z.string(), childSerial: z.string(), childItemId: z.string(),
  parentUnitId: z.nullable(z.string()), parentSerial: z.nullable(z.string()), vendorPartyRef: z.string(),
  vendorTermStart: z.nullable(z.string()), vendorTermEnd: z.nullable(z.string()),
  vendorTermMonths: z.nullable(z.number()), claimMonthOfTerm: z.nullable(z.number()), requestedAmount: z.number(),
  approvedAmount: z.nullable(z.number()), currency: z.string(), status: z.string(), ageDays: z.number(),
  approvedAt: z.nullable(z.string()), approvedBy: z.nullable(z.string()), vendorReference: z.nullable(z.string()),
  vendorNote: z.nullable(z.string()), version: z.string() });
export type RecoveryClaimRow = z.infer<typeof recoveryClaimRow>;
export const recoveryClaimPage = z.object({ rows: z.array(recoveryClaimRow), total: z.number(), skip: z.number(),
  take: z.number() });
export type RecoveryClaimPage = z.infer<typeof recoveryClaimPage>;
export const recoveryCurrencyTotal = z.object({ currency: z.string(), claimCount: z.number(),
  requestedAmount: z.number(), approvedAmount: z.number() });
export type RecoveryCurrencyTotal = z.infer<typeof recoveryCurrencyTotal>;
export const recoveryFilterOption = z.object({ id: z.string(), count: z.number() });
export type RecoveryFilterOption = z.infer<typeof recoveryFilterOption>;
export const oldestOpenRecovery = z.object({ ageDays: z.number(), recoveryNumber: z.string(),
  vendorPartyRef: z.string() });
export type OldestOpenRecovery = z.infer<typeof oldestOpenRecovery>;
export const recoveryClaimSummary = z.object({ openCount: z.number(), openAmounts: z.array(recoveryCurrencyTotal),
  approvedThisYear: z.array(recoveryCurrencyTotal), termExpiredWithoutRecovery: z.number(),
  oldestOpen: z.nullable(oldestOpenRecovery), vendors: z.array(recoveryFilterOption),
  childItems: z.array(recoveryFilterOption) });
export type RecoveryClaimSummary = z.infer<typeof recoveryClaimSummary>;
export const recoveryPolicy = z.object({ wholeUnitLines: recoveryWholeUnitRule,
  campaignClaims: recoveryCampaignClaimsRule, lineKinds: z.array(z.string()), vendorAnswerDueDays: z.number() });
export type RecoveryPolicy = z.infer<typeof recoveryPolicy>;
export const recoveryNotRaisedRow = z.object({ customerClaimId: z.string(), customerClaimNumber: z.string(),
  claimDate: z.string(), approvedAt: z.string(), unitId: z.string(), unitSerial: z.string(), itemId: z.string(),
  reason: recoveryNotRaisedReason, vendorPartyRef: z.nullable(z.string()), vendorTermStart: z.nullable(z.string()),
  vendorTermEnd: z.nullable(z.string()), vendorTermMonths: z.nullable(z.number()),
  claimMonthOfTerm: z.nullable(z.number()) });
export type RecoveryNotRaisedRow = z.infer<typeof recoveryNotRaisedRow>;
export const recoveryNotRaisedReport = z.object({ days: z.number(), rows: z.array(recoveryNotRaisedRow) });
export type RecoveryNotRaisedReport = z.infer<typeof recoveryNotRaisedReport>;
export const recoveryAnswer = z.looseObject({ commandId: z.string(), approvedAmount: z.number(), eventTime: z.string(),
  vendorReference: z.nullable(z.string()), vendorNote: z.nullable(z.string()) });
export type RecoveryAnswer = z.infer<typeof recoveryAnswer>;
export const recoveryAnswerResult = z.object({ recoveryClaimId: z.string(), status: z.string(),
  requestedAmount: z.number(), approvedAmount: z.nullable(z.number()), eventId: z.string(), replayed: z.boolean(),
  vendorReference: z.nullable(z.string()), vendorNote: z.nullable(z.string()) });
export type RecoveryAnswerResult = z.infer<typeof recoveryAnswerResult>;

