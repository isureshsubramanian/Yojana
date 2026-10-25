import * as z from "zod/mini";

const text = z.string();
const nullableText = z.nullable(text);
const count = z.number();
const jsonObject = z.record(text, z.unknown());

export const campaignCoverageSourceSchema = z.enum(["recall", "goodwill"]);
export type CampaignCoverageSource = z.infer<typeof campaignCoverageSourceSchema>;
export const campaignCoverReasonSchema = z.enum(["campaign_ended", "campaign_closed"]);
export type CampaignCoverReason = z.infer<typeof campaignCoverReasonSchema>;
export const claimListRowSchema = z.object({
  claimId: text, claimNumber: text, unitId: text, serial: nullableText, itemId: nullableText,
  claimantPartyRef: text, settlementPartyRef: text, status: text, coverageSource: text, remedy: text,
  serviceCountry: text, currency: text, claimDate: text, submittedAt: text, requestedAmount: count,
  approvedAmount: count, ageDays: count, denialReason: nullableText, recoveryRaised: z.boolean(),
  evidenceRequested: z.boolean(), lineCount: count, linesDecided: count, recoveryVendorNote: nullableText,
  campaignId: nullableText, campaignCode: nullableText, campaignName: nullableText, reserveTag: nullableText,
  campaignCoverReason: z.nullable(campaignCoverReasonSchema),
});
export type ClaimListRow = z.infer<typeof claimListRowSchema>;
export const claimPageSchema = z.object({ items: z.array(claimListRowSchema), total: count, skip: count, take: count });
export type ClaimPage = z.infer<typeof claimPageSchema>;
export const moneyTotalSchema = z.object({ currency: text, amount: count });
export type MoneyTotal = z.infer<typeof moneyTotalSchema>;
export const reasonCountSchema = z.object({ reason: text, count });
export type ReasonCount = z.infer<typeof reasonCountSchema>;
export const claimSummarySchema = z.object({
  asOf: text, open: count, submitted: count, adjudicating: count, approvedNotSettled: count, deniedNotSettled: count,
  approvedThisMonth: z.array(moneyTotalSchema), approvedCountThisMonth: count, partialCountThisMonth: count,
  deniedCountThisMonth: count, deniedReasonsThisMonth: z.array(reasonCountSchema), recoveryOpen: z.array(moneyTotalSchema),
  recoveryOpenCount: count, recoveryVendorCount: count, oldestOpenNumber: nullableText, oldestOpenClaimId: nullableText,
  oldestOpenAgeDays: z.nullable(count), oldestOpenStatus: nullableText, oldestOpenEvidenceRequested: z.boolean(),
  ageBuckets: z.array(count), adjudicationTargetWorkingDays: count, coverageSources: z.array(text),
  serviceCountries: z.array(text), allClaims: count, otherCurrencyClaimCount: count,
});
export type ClaimSummary = z.infer<typeof claimSummarySchema>;

export const claimLineRowSchema = z.object({
  claimLineId: text, sequence: count, unitId: text, serial: nullableText, itemId: nullableText, kind: text,
  coverageFamily: text, description: nullableText, requestedAmount: count, coveragePct: count, approvedAmount: count,
  coverageSource: text, denialReason: nullableText, measurements: z.record(text, count),
});
export type ClaimLineRow = z.infer<typeof claimLineRowSchema>;
export const claimLineDecisionSchema = z.object({
  claimLineId: text, unitId: text, coverageFamily: text, requestedAmount: count, coveragePct: count,
  approvedAmount: count, coverageSource: text, entitlementId: nullableText, denialReason: nullableText,
  chainLength: count, waitingPeriodPurchaseDate: nullableText, firstClaimableDate: nullableText,
  waitingPeriodNotAppliedReason: nullableText,
});
export type ClaimLineDecision = z.infer<typeof claimLineDecisionSchema>;
export const claimEvidenceRowSchema = z.object({ evidenceId: text, kind: text, reference: text, description: nullableText, recordedAt: text, actorRef: text });
export type ClaimEvidenceRow = z.infer<typeof claimEvidenceRowSchema>;
export const claimRecoveryRefSchema = z.object({ recoveryClaimId: text, recoveryNumber: text, vendorPartyRef: text, status: text });
export type ClaimRecoveryRef = z.infer<typeof claimRecoveryRefSchema>;
export const claimDetailSchema = z.object({
  claimId: text, unitId: text, status: text, recoveryRaised: z.boolean(), claimDate: text, claimantPartyRef: text,
  settlementPartyRef: text, serviceEntityId: text, obligorEntityId: text, serviceCountry: text, currency: text,
  coverageSource: text, remedy: text, denialReason: nullableText, chainLength: count, entitlementIds: z.array(text),
  lines: z.array(claimLineDecisionSchema), evidence: z.array(claimEvidenceRowSchema), adjudicatedBy: nullableText,
  reviewerRole: nullableText, successorItemId: nullableText, claimNumber: text, serial: nullableText, itemId: nullableText,
  submittedAt: text, submittedBy: text, adjudicatedAt: nullableText, lineRows: z.nullable(z.array(claimLineRowSchema)),
  recoveries: z.nullable(z.array(claimRecoveryRefSchema)), openWork: nullableText, campaignId: nullableText,
  campaignCode: nullableText, campaignName: nullableText, reserveTag: nullableText,
  campaignCoverReason: z.nullable(campaignCoverReasonSchema), version: text, reportedCauseCode: nullableText,
  reportedDescription: nullableText, occurredDate: nullableText,
});
export type ClaimDetail = z.infer<typeof claimDetailSchema>;

export const policyStackStepSchema = z.object({ level: text, outcome: text, detail: text });
export type PolicyStackStep = z.infer<typeof policyStackStepSchema>;
export const claimLinePreviewSchema = z.object({
  claimLineId: text, sequence: count, unitId: text, serial: nullableText, itemId: nullableText, isChild: z.boolean(),
  kind: text, coverageFamily: text, description: nullableText, requestedAmount: count, coveragePct: count,
  approvedAmount: count, coverageSource: text, level: text, policyCode: nullableText, policyVersion: z.nullable(count),
  entitlementId: nullableText, coverStart: nullableText, coverEnd: nullableText, denialReason: nullableText,
  stack: z.array(policyStackStepSchema), waitingPeriodPurchaseDate: nullableText, firstClaimableDate: nullableText,
  waitingPeriodNotAppliedReason: nullableText,
});
export type ClaimLinePreview = z.infer<typeof claimLinePreviewSchema>;
export const claimCheckSchema = z.object({ key: text, value: text, outcome: text });
export type ClaimCheck = z.infer<typeof claimCheckSchema>;
export const recoveryPreviewSchema = z.object({ childUnitId: text, childSerial: nullableText, vendorPartyRef: text, requestedAmount: count, termStart: nullableText, termEnd: nullableText, termMonths: z.nullable(count), monthOfTerm: z.nullable(count) });
export type RecoveryPreview = z.infer<typeof recoveryPreviewSchema>;
export const recoveryNotRaisedReasonSchema = z.enum(["vendor_term_ended", "no_vendor_term", "recall_claim", "campaign_claim", "several_covered_parts", "part_not_named", "line_kind_not_recovered"]);

export const CLAIM_RISK_RULE_CODES = ["recent_cover_purchase", "repeat_unit_claims", "repeat_fault", "claimant_rate", "channel_mismatch", "recent_ownership_change", "part_not_returned", "duplicate_open_claim"] as const;
export const claimRiskRuleCodeSchema = z.enum(CLAIM_RISK_RULE_CODES);
export type ClaimRiskRuleCode = z.infer<typeof claimRiskRuleCodeSchema>;
export const claimRiskFlagStatusSchema = z.enum(["open", "cleared", "confirmed"]);
export type ClaimRiskFlagStatus = z.infer<typeof claimRiskFlagStatusSchema>;
export const claimRiskDecisionSchema = z.enum(["cleared", "confirmed"]);
export type ClaimRiskDecision = z.infer<typeof claimRiskDecisionSchema>;
export const claimRiskOpenFlagSchema = z.object({ flagId: text, ruleCode: claimRiskRuleCodeSchema, raisedAt: text });
export type ClaimRiskOpenFlag = z.infer<typeof claimRiskOpenFlagSchema>;
export const claimAdjudicationPreviewSchema = z.object({
  claimId: text, claimNumber: text, outcome: text, coverageSource: text, currency: text, requestedAmount: count,
  approvedAmount: count, lines: z.array(claimLinePreviewSchema), checks: z.array(claimCheckSchema), cohortId: nullableText,
  obligorEntityId: text, recoveries: z.array(recoveryPreviewSchema), chargeableAmount: count, refusal: nullableText,
  refusalStep: nullableText, requiredReviewerRole: nullableText, successorItemId: nullableText,
  manualDenialReasons: z.array(text), recoveryNotRaisedReason: z.nullable(recoveryNotRaisedReasonSchema),
  campaignId: nullableText, campaignCode: nullableText, campaignName: nullableText, reserveTag: nullableText,
  campaignCoverReason: z.nullable(campaignCoverReasonSchema), openRiskFlags: z.nullable(z.array(claimRiskOpenFlagSchema)),
});
export type ClaimAdjudicationPreview = z.infer<typeof claimAdjudicationPreviewSchema>;

export const claimRiskEnabledRuleSchema = z.object({ enabled: z.boolean() });
export type ClaimRiskEnabledRule = z.infer<typeof claimRiskEnabledRuleSchema>;
export const claimRiskDaysRuleSchema = z.extend(claimRiskEnabledRuleSchema, { days: count });
export type ClaimRiskDaysRule = z.infer<typeof claimRiskDaysRuleSchema>;
export const claimRiskRepeatRuleSchema = z.extend(claimRiskDaysRuleSchema, { minimumEarlierClaims: count });
export type ClaimRiskRepeatRule = z.infer<typeof claimRiskRepeatRuleSchema>;
export const claimRiskRateRuleSchema = z.extend(claimRiskDaysRuleSchema, { rateMultiplier: count, minimumClaims: count, minimumUnits: count });
export type ClaimRiskRateRule = z.infer<typeof claimRiskRateRuleSchema>;
export const claimRiskRulesPolicySchema = z.object({ recentCoverPurchase: claimRiskDaysRuleSchema, repeatUnitClaims: claimRiskRepeatRuleSchema, repeatFault: claimRiskDaysRuleSchema, claimantRate: claimRiskRateRuleSchema, channelMismatch: claimRiskEnabledRuleSchema, recentOwnershipChange: claimRiskDaysRuleSchema, partNotReturned: claimRiskEnabledRuleSchema, duplicateOpenClaim: claimRiskEnabledRuleSchema, reviewerRole: text, requireDifferentReviewer: z.boolean() });
export type ClaimRiskRulesPolicy = z.infer<typeof claimRiskRulesPolicySchema>;
export const claimRiskFlagRowSchema = z.object({ flagId: text, claimId: text, claimNumber: text, ruleCode: claimRiskRuleCodeSchema, status: claimRiskFlagStatusSchema, thresholds: jsonObject, evidence: jsonObject, raisedAt: text, raisedByEvaluation: text, decidedAt: nullableText, reviewerName: nullableText, decisionReason: nullableText });
export type ClaimRiskFlagRow = z.infer<typeof claimRiskFlagRowSchema>;
export const claimRiskFlagPageSchema = z.object({ items: z.array(claimRiskFlagRowSchema), nextCursor: nullableText, hasMore: z.boolean(), take: count });
export type ClaimRiskFlagPage = z.infer<typeof claimRiskFlagPageSchema>;
export const claimRiskReviewFlagSchema = z.object({ flagId: text, ruleCode: claimRiskRuleCodeSchema, status: claimRiskFlagStatusSchema, raisedAt: text });
export type ClaimRiskReviewFlag = z.infer<typeof claimRiskReviewFlagSchema>;
export const claimRiskReviewRowSchema = z.object({ claimId: text, claimNumber: text, unitId: text, serial: nullableText, itemId: nullableText, claimantPartyRef: text, remedy: text, currency: text, requestedAmount: count, flags: z.array(claimRiskReviewFlagSchema), firstRaisedAt: text });
export type ClaimRiskReviewRow = z.infer<typeof claimRiskReviewRowSchema>;
export const claimRiskReviewPageSchema = z.object({ items: z.array(claimRiskReviewRowSchema), nextCursor: nullableText, hasMore: z.boolean(), take: count });
export type ClaimRiskReviewPage = z.infer<typeof claimRiskReviewPageSchema>;
export const claimRiskReviewSummarySchema = z.object({ heldClaims: count, openFlags: count, oldestOpenFlagRaisedAt: nullableText, claimsSubmitted: count, claimsFlagged: count, claimsFlaggedRate: count, flagsRaised: count, flagsConfirmed: count, confirmationRate: count, claimsDeniedAfterConfirmedFlag: count, medianHoursToDecision: z.nullable(count) });
export type ClaimRiskReviewSummary = z.infer<typeof claimRiskReviewSummarySchema>;
export const claimRiskFlagReportRowSchema = z.object({ ruleCode: claimRiskRuleCodeSchema, hits: count, cleared: count, confirmed: count, confirmationRate: count, confirmedClaimsDeniedAfterReview: count, distinctClaimsDeniedAfterConfirmedFlag: count });
export type ClaimRiskFlagReportRow = z.infer<typeof claimRiskFlagReportRowSchema>;
export const claimRiskFlagDecisionResultSchema = z.object({ flagId: text, claimId: text, claimNumber: text, ruleCode: claimRiskRuleCodeSchema, status: claimRiskDecisionSchema, decidedAt: text, reason: text });
export type ClaimRiskFlagDecisionResult = z.infer<typeof claimRiskFlagDecisionResultSchema>;

export const claimLineInputSchema = z.object({ kind: text, coverageFamily: text, requestedAmount: count, unitId: nullableText, description: nullableText, measurements: z.nullable(z.record(text, count)) });
export type ClaimLineInput = z.infer<typeof claimLineInputSchema>;
export const claimEvidenceInputSchema = z.object({ kind: text, reference: text, description: nullableText });
export type ClaimEvidenceInput = z.infer<typeof claimEvidenceInputSchema>;
const documentSerialSchema = z.object({ value: text, issuerPartyRef: nullableText, identifierType: nullableText, erpSerialId: nullableText });
export const submitClaimSchema = z.object({
  reportedCauseCode: z.optional(nullableText), reportedDescription: z.optional(nullableText), occurredDate: z.optional(nullableText),
  commandId: text, unitId: nullableText, identifier: z.nullable(documentSerialSchema), claimDate: text,
  claimantPartyRef: text, settlementPartyRef: text, serviceEntityId: text, obligorEntityId: text,
  serviceCountry: text, currency: text, lines: z.array(claimLineInputSchema), evidence: z.array(claimEvidenceInputSchema),
  remedy: text, consumerChoiceEvidenceRef: text, eventTime: text, proofOfPurchaseRef: nullableText,
  proofOfPurchaseDate: nullableText, itemId: nullableText, campaignId: z.optional(text),
});
export type SubmitClaim = z.infer<typeof submitClaimSchema>;
export const submitClaimResultSchema = z.object({ claimId: text, unitId: text, status: text, claimLineIds: z.array(text), eventId: text, replayed: z.boolean() });
export type SubmitClaimResult = z.infer<typeof submitClaimResultSchema>;
export const claimChangeResultSchema = z.object({ claimId: text, status: text, eventId: text, replayed: z.boolean() });
export type ClaimChangeResult = z.infer<typeof claimChangeResultSchema>;

export const currencyUsageSchema = z.object({ kind: text, count });
export type CurrencyUsage = z.infer<typeof currencyUsageSchema>;
export const otherCurrencyUsageSchema = z.extend(currencyUsageSchema, { currency: text });
export type OtherCurrencyUsage = z.infer<typeof otherCurrencyUsageSchema>;
export const tenantCurrencySchema = z.object({ currency: nullableText, changeable: z.boolean(), usage: z.array(currencyUsageSchema) });
export type TenantCurrency = z.infer<typeof tenantCurrencySchema>;
export const currencyUsageReportSchema = z.object({ currency: nullableText, rows: z.array(otherCurrencyUsageSchema) });
export type CurrencyUsageReport = z.infer<typeof currencyUsageReportSchema>;
export const currencyInfoSchema = z.object({ code: text, name: text, symbol: text, minorDigits: count });
export type CurrencyInfo = z.infer<typeof currencyInfoSchema>;
export const coverageFamilySchema = z.object({ familyCode: text, name: text, stacking: z.boolean(), accountingTag: text, active: z.boolean(), version: text });
export type CoverageFamily = z.infer<typeof coverageFamilySchema>;
export const claimCampaignOptionSchema = z.object({ campaignId: text, code: text, name: text, coverageSource: campaignCoverageSourceSchema, reserveTag: text, reasonCode: text, runsUntil: nullableText, coversOnDate: z.boolean(), notCoveredReason: z.nullable(campaignCoverReasonSchema), partsPct: count, labourPct: count, travelPct: count });
export type ClaimCampaignOption = z.infer<typeof claimCampaignOptionSchema>;
