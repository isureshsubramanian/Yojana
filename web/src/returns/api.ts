import * as z from "zod/mini";
import { apiParsed } from "../api/client.ts";
import { newCommandId, now } from "../api/units.ts";
import { closeRmaResult, rmaDetail, rmaListResult, warrantyReplacementDetail, type ReplacementCreateDraft } from "./types.ts";

export interface RmaFilters { stage: string; minimumAgeDays: number | null; maximumAgeDays: number | null; exceptionsOnly: boolean; q: string }

function query(values: Record<string, string | number | boolean | null | undefined>): string {
  const params = new URLSearchParams();
  for (const [key, value] of Object.entries(values)) if (value !== null && value !== undefined && value !== "") params.set(key, String(value));
  const text = params.toString(); return text ? `?${text}` : "";
}

export const returnsApi = {
  list: (filters: RmaFilters, after: string | null, take: number, signal?: AbortSignal) =>
    apiParsed(`/api/rmas/${query({ ...filters, after, take })}`, rmaListResult, { signal }),
  detail: (id: string, signal?: AbortSignal) => apiParsed(`/api/rmas/${id}`, rmaDetail, { signal }),
  authorise: (id: string) => apiParsed(`/api/rmas/${id}/authorise`, z.unknown(), { method: "POST", body: { commandId: newCommandId(), rmaId: id, eventTime: now() } }),
  dispatch: (id: string, body: { dispatchedAt: string; carrierName: string | null; trackingReference: string | null }) =>
    apiParsed(`/api/rmas/${id}/dispatch`, z.unknown(), { method: "POST", body: { commandId: newCommandId(), rmaId: id, ...body } }),
  receive: (id: string, body: { receivingLocationId: string; dispatchedAt: string | null; receivedAt: string; lines: { rmaLineId: string; receivedIdentifier: { value: string; issuerPartyRef: null; identifierType: null; erpSerialId: null } | null; conditionGrade: string; valuationRule: string; outcome: string; exceptionNote: string | null }[]; confirmedNotModelNumber?: boolean; modelNumberConfirmationReason?: string | null }) =>
    apiParsed(`/api/rmas/${id}/receive`, z.unknown(), { method: "POST", body: { commandId: newCommandId(), rmaId: id, ...body, eventTime: body.receivedAt } }),
  disposition: (id: string, lines: { rmaLineId: string; disposition: string; vendorPartyRef: string | null }[]) =>
    apiParsed(`/api/rmas/${id}/disposition`, z.unknown(), { method: "POST", body: { commandId: newCommandId(), rmaId: id, lines, effectiveDate: new Date().toISOString().slice(0, 10), eventTime: now() } }),
  close: (id: string) => apiParsed(`/api/rmas/${id}/close`, closeRmaResult, {
    method: "POST", body: { commandId: newCommandId(), rmaId: id, eventTime: now() }, acceptedStatuses: [409],
  }),
};

const createWarrantyReplacementResult = z.object({ document: warrantyReplacementDetail, replayed: z.boolean() });

export const replacementApi = {
  detail: (id: string, signal?: AbortSignal) => apiParsed(`/api/warranty-replacements/${id}`, warrantyReplacementDetail, { signal }),
  create: (draft: ReplacementCreateDraft) => apiParsed("/api/warranty-replacements/", createWarrantyReplacementResult, {
    method: "POST", body: {
      commandId: newCommandId(), ...draft, differenceAmount: draft.pricePolicy === "zero" ? 0 : draft.differenceAmount,
      replacementPolicyCode: draft.replacementPolicyCode || null, returnDue: draft.returnDue || null,
      nonReturnPriceBasis: draft.returnDue ? draft.nonReturnPriceBasis : null,
      replacementConditionAllowed: replacementConditionAllowed(draft.replacementReason), eventTime: now(),
    },
  }),
  ship: (id: string) => apiParsed(`/api/warranty-replacements/${id}/ship`, z.unknown(), { method: "POST", body: { commandId: newCommandId(), warrantyReplacementId: id, replacementDate: new Date().toISOString().slice(0, 10), eventTime: now() } }),
};

function replacementConditionAllowed(reason: string): string {
  if (reason === "dead_on_arrival" || reason === "seller_error") return "new";
  if (reason === "warranty_defect") return "new_or_refurbished";
  return "any";
}
