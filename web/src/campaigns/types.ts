export type CampaignCriterionKind = "identifier_range" | "child" | "firmware" | "lot";
export type CampaignReason = "safety" | "reliability" | "firmware" | "regulatory" | "goodwill";

export interface CampaignCriterion {
  criterionId: string;
  kind: CampaignCriterionKind;
  scheme: string | null;
  rangeStart: string | null;
  rangeEnd: string | null;
  lotId: string | null;
  childUnitId: string | null;
  childItemId: string | null;
  firmwareValue: string | null;
  effectiveAt: string | null;
  childLotId: string | null;
  firmwareKey: string | null;
}

export interface Campaign {
  campaignId: string; code: string; name: string; coverageSource: "recall" | "goodwill"; reserveTag: string;
  partsPct: number; labourPct: number; travelPct: number;
  status: "draft" | "selected" | "active" | "closed"; criteria: CampaignCriterion[];
  createdAt: string; createdBy: string; updatedAt: string; updatedBy: string; selectedAt: string | null;
  reasonCode: CampaignReason; reasonNote: string | null; runsUntil: string | null; version: string;
}

export interface CampaignListRow { campaignId: string; code: string; name: string; coverageSource: string; status: string; selectedUnits: number; selectedAt: string | null; reserveTag: string; reasonCode: CampaignReason; runsUntil: string | null; partsPct: number; labourPct: number; travelPct: number }
export interface Page<T> { items: T[]; total: number; skip: number; take: number }

export interface CampaignMatchRow {
  unitId: string; serial: string | null; itemId: string; ownerPartyRef: string | null; ownerPartyType: string | null;
  custodianPartyRef: string | null; custodianPartyType: string | null; custodyKind: string | null;
  matchedChildSerial: string | null; firmwareKey: string | null; firmwareValue: string | null; firmwareSince: string | null;
}
export interface CampaignPreview { campaignId: string; page: Page<CampaignMatchRow> }
export interface CampaignFunnelStep { kind: CampaignCriterionKind; count: number }
export interface CampaignMatchEvidence { kind: CampaignCriterionKind; label: string; value: string }

export interface CampaignUnitRow {
  unitId: string; serial: string | null; ownerPartyRef: string | null; ownerPartyType: string | null; ownerSince: string | null;
  custodianPartyRef: string | null; custodianPartyType: string | null; custodyKind: string | null; custodyAtSelection: string | null; custodySince: string | null;
  invoicePartyRef: string | null; selectedAt: string; selectedBy: string; claimId: string | null; claimNumber: string | null; claimStatus: string | null;
}
export interface CampaignUnitsSummary {
  unitsSelected: number; selectedAt: string | null; addedSinceFirstSelection: number; recipients: number; owners: number;
  custodiansNotOwner: number; ownerDiffersFromInvoice: number; repaired: number; inOurHands: number;
}
export interface SelectCampaignResult { campaignId: string; matched: number; added: number; selected: number; eventBatches: number; replayed: boolean }

export interface CampaignDraft {
  code: string; name: string; coverageSource: "recall" | "goodwill"; reserveTag: string; status: Campaign["status"];
  partsPct: number; labourPct: number; travelPct: number;
  reasonCode: CampaignReason; reasonNote: string; runsUntil: string;
  criteria: CampaignCriterion[];
}
