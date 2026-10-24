import { api } from "../api/client";
import { newCommandId, now } from "../api/units";
import type { CloseRmaResult, ReplacementCreateDraft, RmaDetail, RmaListResult, WarrantyReplacementDetail } from "./types";

export interface RmaFilters { stage: string; minimumAgeDays: number | null; maximumAgeDays: number | null; exceptionsOnly: boolean; q: string }

function query(values: Record<string, string | number | boolean | null | undefined>): string {
  const params = new URLSearchParams();
  for (const [key, value] of Object.entries(values)) if (value !== null && value !== undefined && value !== "") params.set(key, String(value));
  const text = params.toString(); return text ? `?${text}` : "";
}

export const returnsApi = {
  list: (filters: RmaFilters, after: string | null, take: number, signal?: AbortSignal) =>
    api<RmaListResult>(`/api/rmas/${query({ ...filters, after, take })}`, { signal }),
  detail: (id: string, signal?: AbortSignal) => api<RmaDetail>(`/api/rmas/${id}`, { signal }),
  authorise: (id: string) => api<unknown>(`/api/rmas/${id}/authorise`, { method: "POST", body: { commandId: newCommandId(), rmaId: id, eventTime: now() } }),
  dispatch: (id: string, body: { dispatchedAt: string; carrierName: string | null; trackingReference: string | null }) =>
    api<unknown>(`/api/rmas/${id}/dispatch`, { method: "POST", body: { commandId: newCommandId(), rmaId: id, ...body } }),
  receive: (id: string, body: { receivingLocationId: string; dispatchedAt: string | null; receivedAt: string; lines: { rmaLineId: string; receivedIdentifier: { value: string; issuerPartyRef: null; identifierType: null; erpSerialId: null } | null; conditionGrade: string; valuationRule: string; outcome: string; exceptionNote: string | null }[]; confirmedNotModelNumber?: boolean; modelNumberConfirmationReason?: string | null }) =>
    api<unknown>(`/api/rmas/${id}/receive`, { method: "POST", body: { commandId: newCommandId(), rmaId: id, ...body, eventTime: body.receivedAt } }),
  disposition: (id: string, lines: { rmaLineId: string; disposition: string; vendorPartyRef: string | null }[]) =>
    api<unknown>(`/api/rmas/${id}/disposition`, { method: "POST", body: { commandId: newCommandId(), rmaId: id, lines, effectiveDate: new Date().toISOString().slice(0, 10), eventTime: now() } }),
  close: (id: string) => api<CloseRmaResult>(`/api/rmas/${id}/close`, {
    method: "POST", body: { commandId: newCommandId(), rmaId: id, eventTime: now() }, acceptedStatuses: [409],
  }),
};

export const replacementApi = {
  detail: (id: string, signal?: AbortSignal) => api<WarrantyReplacementDetail>(`/api/warranty-replacements/${id}`, { signal }),
  create: (draft: ReplacementCreateDraft) => api<{ document: WarrantyReplacementDetail; replayed: boolean }>("/api/warranty-replacements/", {
    method: "POST", body: {
      commandId: newCommandId(), ...draft, differenceAmount: draft.pricePolicy === "zero" ? 0 : draft.differenceAmount,
      replacementPolicyCode: draft.replacementPolicyCode || null, returnDue: draft.returnDue || null,
      nonReturnPriceBasis: draft.returnDue ? draft.nonReturnPriceBasis : null,
      replacementConditionAllowed: replacementConditionAllowed(draft.replacementReason), eventTime: now(),
    },
  }),
  ship: (id: string) => api<unknown>(`/api/warranty-replacements/${id}/ship`, { method: "POST", body: { commandId: newCommandId(), warrantyReplacementId: id, replacementDate: new Date().toISOString().slice(0, 10), eventTime: now() } }),
};

function replacementConditionAllowed(reason: string): string {
  if (reason === "dead_on_arrival" || reason === "seller_error") return "new";
  if (reason === "warranty_defect") return "new_or_refurbished";
  return "any";
}
