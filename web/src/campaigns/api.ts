import { api } from "../api/client";
import { DEFAULT_LEGAL_ENTITY, newCommandId, now } from "../api/units";
import type { Campaign, CampaignDraft, CampaignFunnelStep, CampaignListRow, CampaignMatchEvidence, CampaignPreview, CampaignUnitRow, CampaignUnitsSummary, Page, SelectCampaignResult } from "./types";

const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

function query(values: Record<string, string | number | boolean | null | undefined>): string {
  const params = new URLSearchParams();
  for (const [key, value] of Object.entries(values)) if (value !== undefined && value !== null && value !== "") params.set(key, String(value));
  const text = params.toString(); return text ? `?${text}` : "";
}

function command(draft: CampaignDraft) {
  return {
    code: draft.code, name: draft.name, coverageSource: draft.coverageSource, reserveTag: draft.reserveTag,
    reasonCode: draft.reasonCode, reasonNote: draft.reasonNote.trim() || null, runsUntil: draft.runsUntil || null,
    criteria: draft.criteria.map(({ kind, scheme, rangeStart, rangeEnd, lotId, childUnitId, childItemId, firmwareValue, effectiveAt, childLotId, firmwareKey }) =>
      ({ kind, scheme, rangeStart, rangeEnd, lotId: guid(lotId), childUnitId: guid(childUnitId), childItemId: guid(childItemId), firmwareValue, effectiveAt, childLotId: guid(childLotId), firmwareKey })),
    eventTime: now(),
  };
}

export const campaignApi = {
  list: (status: string | null, skip: number, take: number, signal?: AbortSignal) => api<Page<CampaignListRow>>(`/api/campaigns/${query({ status, skip, take })}`, { signal }),
  detail: (id: string, signal?: AbortSignal) => api<Campaign>(`/api/campaigns/${id}`, { signal }),
  create: (draft: CampaignDraft) => api<{ campaign: Campaign; replayed: boolean }>("/api/campaigns/", { method: "POST", body: { commandId: newCommandId(), ...command(draft) } }),
  update: (id: string, draft: CampaignDraft) => api<{ campaign: Campaign; replayed: boolean }>(`/api/campaigns/${id}`, { method: "PUT", body: { commandId: newCommandId(), campaignId: id, status: draft.status, ...command(draft) } }),
  preview: (id: string, skip: number, take: number, signal?: AbortSignal) => api<CampaignPreview>(`/api/campaigns/${id}/preview`, { method: "POST", body: { campaignId: id, skip, take }, signal }),
  funnel: (id: string, signal?: AbortSignal) => api<CampaignFunnelStep[]>(`/api/campaigns/${id}/funnel`, { signal }),
  match: (id: string, unitId: string, signal?: AbortSignal) => api<CampaignMatchEvidence[]>(`/api/campaigns/${id}/units/${unitId}/match`, { signal }),
  select: (id: string) => api<SelectCampaignResult>(`/api/campaigns/${id}/select`, { method: "POST", body: { commandId: newCommandId(), campaignId: id, eventTime: now(), legalEntityId: DEFAULT_LEGAL_ENTITY } }),
  units: (id: string, filters: Record<string, string | number | boolean | null | undefined>, signal?: AbortSignal) => api<Page<CampaignUnitRow>>(`/api/campaigns/${id}/units${query(filters)}`, { signal }),
  unitsSummary: (id: string, signal?: AbortSignal) => api<CampaignUnitsSummary>(`/api/campaigns/${id}/units/summary`, { signal }),
  exportUrl: (id: string) => `/api/campaigns/${id}/units/export`,
};

function guid(value: string | null): string | null { return value && GUID.test(value) ? value : null; }
