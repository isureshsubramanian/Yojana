import * as z from "zod/mini";
import { apiParsed } from "../api/client.ts";
import { newCommandId, now } from "../api/units.ts";
import {
  campaignCriterionKindSchema, campaignDraftSchema, campaignFunnelStepSchema, campaignListRowSchema,
  campaignMatchEvidenceSchema, campaignPreviewSchema, campaignSchema, campaignUnitRowSchema,
  campaignUnitsSummarySchema, pageSchema, selectCampaignResultSchema, type CampaignDraft,
} from "./types.ts";

const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const nullableText = z.nullable(z.string());
const campaignCriterionInputSchema = z.object({
  kind: campaignCriterionKindSchema, scheme: nullableText, rangeStart: nullableText, rangeEnd: nullableText,
  lotId: nullableText, childUnitId: nullableText, childItemId: nullableText, firmwareValue: nullableText,
  effectiveAt: nullableText, childLotId: nullableText, firmwareKey: nullableText,
});
const campaignCommandSchema = z.object({
  commandId: z.string(), code: z.string(), name: z.string(), coverageSource: z.enum(["recall", "goodwill"]),
  reserveTag: z.string(), partsPct: z.number(), labourPct: z.number(), travelPct: z.number(),
  reasonCode: z.enum(["safety", "reliability", "firmware", "regulatory", "goodwill"]),
  reasonNote: nullableText, runsUntil: nullableText, criteria: z.array(campaignCriterionInputSchema), eventTime: z.string(),
});
const updateCampaignCommandSchema = z.extend(campaignCommandSchema, { campaignId: z.string(), status: campaignDraftSchema.shape.status, expectedVersion: z.string() });
const previewCampaignSchema = z.object({ campaignId: z.string(), skip: z.number(), take: z.number() });
const selectCampaignSchema = z.object({ commandId: z.string(), campaignId: z.string(), eventTime: z.string() });
const campaignResultSchema = z.object({ campaign: campaignSchema, replayed: z.boolean() });

function query(values: Record<string, string | number | boolean | null | undefined>): string {
  const params = new URLSearchParams();
  for (const [key, value] of Object.entries(values)) if (value !== undefined && value !== null && value !== "") params.set(key, String(value));
  const text = params.toString(); return text ? `?${text}` : "";
}

function command(draft: CampaignDraft) {
  return {
    code: draft.code, name: draft.name, coverageSource: draft.coverageSource, reserveTag: draft.reserveTag,
    partsPct: draft.partsPct, labourPct: draft.labourPct, travelPct: draft.travelPct,
    reasonCode: draft.reasonCode, reasonNote: draft.reasonNote.trim() || null, runsUntil: draft.runsUntil || null,
    criteria: draft.criteria.map(({ kind, scheme, rangeStart, rangeEnd, lotId, childUnitId, childItemId, firmwareValue, effectiveAt, childLotId, firmwareKey }) =>
      ({ kind, scheme, rangeStart, rangeEnd, lotId: guid(lotId), childUnitId: guid(childUnitId), childItemId: guid(childItemId), firmwareValue, effectiveAt, childLotId: guid(childLotId), firmwareKey })),
    eventTime: now(),
  };
}

export const campaignApi = {
  list: (status: string | null, skip: number, take: number, signal?: AbortSignal) => apiParsed(`/api/campaigns/${query({ status, skip, take })}`, pageSchema(campaignListRowSchema), { signal }),
  detail: (id: string, signal?: AbortSignal) => apiParsed(`/api/campaigns/${id}`, campaignSchema, { signal }),
  create: (draft: CampaignDraft) => { const body = { commandId: newCommandId(), ...command(draft) }; return apiParsed("/api/campaigns/", campaignResultSchema, { method: "POST", body: z.parse(campaignCommandSchema, body) }); },
  update: (id: string, draft: CampaignDraft, expectedVersion: string) => { const body = { commandId: newCommandId(), campaignId: id, status: draft.status, expectedVersion, ...command(draft) }; return apiParsed(`/api/campaigns/${id}`, campaignResultSchema, { method: "PUT", body: z.parse(updateCampaignCommandSchema, body) }); },
  preview: (id: string, skip: number, take: number, signal?: AbortSignal) => { const body = { campaignId: id, skip, take }; return apiParsed(`/api/campaigns/${id}/preview`, campaignPreviewSchema, { method: "POST", body: z.parse(previewCampaignSchema, body), signal }); },
  funnel: (id: string, signal?: AbortSignal) => apiParsed(`/api/campaigns/${id}/funnel`, z.array(campaignFunnelStepSchema), { signal }),
  match: (id: string, unitId: string, signal?: AbortSignal) => apiParsed(`/api/campaigns/${id}/units/${unitId}/match`, z.array(campaignMatchEvidenceSchema), { signal }),
  select: (id: string) => { const body = { commandId: newCommandId(), campaignId: id, eventTime: now() }; return apiParsed(`/api/campaigns/${id}/select`, selectCampaignResultSchema, { method: "POST", body: z.parse(selectCampaignSchema, body) }); },
  units: (id: string, filters: Record<string, string | number | boolean | null | undefined>, signal?: AbortSignal) => apiParsed(`/api/campaigns/${id}/units${query(filters)}`, pageSchema(campaignUnitRowSchema), { signal }),
  unitsSummary: (id: string, signal?: AbortSignal) => apiParsed(`/api/campaigns/${id}/units/summary`, campaignUnitsSummarySchema, { signal }),
  exportUrl: (id: string) => `/api/campaigns/${id}/units/export`,
};

function guid(value: string | null): string | null { return value && GUID.test(value) ? value : null; }
