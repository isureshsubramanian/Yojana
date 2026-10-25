import * as z from "zod/mini";

const text = z.string();
const nullableText = z.nullable(text);
const count = z.number();

export const campaignCriterionKindSchema = z.enum(["identifier_range", "child", "firmware", "lot"]);
export const campaignReasonSchema = z.enum(["safety", "reliability", "firmware", "regulatory", "goodwill"]);
export type CampaignCriterionKind = z.infer<typeof campaignCriterionKindSchema>;
export type CampaignReason = z.infer<typeof campaignReasonSchema>;

export const campaignCriterionSchema = z.object({
  criterionId: text, kind: campaignCriterionKindSchema, scheme: nullableText, rangeStart: nullableText,
  rangeEnd: nullableText, lotId: nullableText, childUnitId: nullableText, childItemId: nullableText,
  firmwareValue: nullableText, effectiveAt: nullableText, childLotId: nullableText, firmwareKey: nullableText,
});
export type CampaignCriterion = z.infer<typeof campaignCriterionSchema>;

export const campaignSchema = z.object({
  campaignId: text, code: text, name: text, coverageSource: z.enum(["recall", "goodwill"]), reserveTag: text,
  partsPct: count, labourPct: count, travelPct: count,
  status: z.enum(["draft", "selected", "active", "closed"]), criteria: z.array(campaignCriterionSchema),
  createdAt: text, createdBy: text, updatedAt: text, updatedBy: text, selectedAt: nullableText,
  reasonCode: campaignReasonSchema, reasonNote: nullableText, runsUntil: nullableText, version: text,
});
export type Campaign = z.infer<typeof campaignSchema>;

export const campaignListRowSchema = z.object({
  campaignId: text, code: text, name: text, coverageSource: text, status: text, selectedUnits: count,
  selectedAt: nullableText, reserveTag: text, reasonCode: campaignReasonSchema, runsUntil: nullableText,
  partsPct: count, labourPct: count, travelPct: count,
});
export type CampaignListRow = z.infer<typeof campaignListRowSchema>;

export const pageSchema = <T extends z.ZodMiniType>(item: T) => z.object({ items: z.array(item), total: count, skip: count, take: count });
export type Page<T> = { items: T[]; total: number; skip: number; take: number };

export const campaignMatchRowSchema = z.object({
  unitId: text, serial: nullableText, itemId: text, ownerPartyRef: nullableText, ownerPartyType: nullableText,
  custodianPartyRef: nullableText, custodianPartyType: nullableText, custodyKind: nullableText,
  matchedChildSerial: nullableText, firmwareKey: nullableText, firmwareValue: nullableText, firmwareSince: nullableText,
});
export type CampaignMatchRow = z.infer<typeof campaignMatchRowSchema>;
export const campaignPreviewSchema = z.object({ campaignId: text, page: pageSchema(campaignMatchRowSchema) });
export type CampaignPreview = z.infer<typeof campaignPreviewSchema>;
export const campaignFunnelStepSchema = z.object({ kind: campaignCriterionKindSchema, count });
export type CampaignFunnelStep = z.infer<typeof campaignFunnelStepSchema>;
export const campaignMatchEvidenceSchema = z.object({ kind: campaignCriterionKindSchema, label: text, value: text });
export type CampaignMatchEvidence = z.infer<typeof campaignMatchEvidenceSchema>;

export const campaignUnitRowSchema = z.object({
  unitId: text, serial: nullableText, ownerPartyRef: nullableText, ownerPartyType: nullableText, ownerSince: nullableText,
  custodianPartyRef: nullableText, custodianPartyType: nullableText, custodyKind: nullableText,
  custodyAtSelection: nullableText, custodySince: nullableText, invoicePartyRef: nullableText, selectedAt: text,
  selectedBy: text, claimId: nullableText, claimNumber: nullableText, claimStatus: nullableText,
});
export type CampaignUnitRow = z.infer<typeof campaignUnitRowSchema>;
export const campaignUnitsSummarySchema = z.object({
  unitsSelected: count, selectedAt: nullableText, addedSinceFirstSelection: count, recipients: count, owners: count,
  custodiansNotOwner: count, ownerDiffersFromInvoice: count, repaired: count, inOurHands: count,
});
export type CampaignUnitsSummary = z.infer<typeof campaignUnitsSummarySchema>;
export const selectCampaignResultSchema = z.object({ campaignId: text, matched: count, added: count, selected: count, eventBatches: count, replayed: z.boolean() });
export type SelectCampaignResult = z.infer<typeof selectCampaignResultSchema>;

export const campaignDraftSchema = z.object({
  code: text, name: text, coverageSource: z.enum(["recall", "goodwill"]), reserveTag: text,
  status: z.enum(["draft", "selected", "active", "closed"]), partsPct: count, labourPct: count, travelPct: count,
  reasonCode: campaignReasonSchema, reasonNote: text, runsUntil: text, criteria: z.array(campaignCriterionSchema),
});
export type CampaignDraft = z.infer<typeof campaignDraftSchema>;
