import * as z from "zod/mini";
import { apiParsed } from "../api/client.ts";
import { newCommandId, now } from "../api/units.ts";
import { refurbishmentPolicy, refurbishmentWorkOrderPage, refurbishmentWorkOrderRow, sanitisationRow, unitAttributeHistoryRow, unitTestRow, usageReadingRow, type RefurbishmentPolicy } from "./types.ts";

function query(values: Record<string, string | number | null | undefined>): string {
  const params = new URLSearchParams();
  for (const [key, value] of Object.entries(values)) if (value !== null && value !== undefined && value !== "") params.set(key, String(value));
  const text = params.toString(); return text ? `?${text}` : "";
}

const base = (workOrderId: string) => `/api/refurbishments/${workOrderId}`;
export const refurbishmentsApi = {
  list: (status: string, kind: string, skip: number, take: number, signal?: AbortSignal) => apiParsed(`/api/refurbishments/${query({ status, kind, skip, take })}`, refurbishmentWorkOrderPage, { signal }),
  forUnit: (unitId: string, signal?: AbortSignal) => apiParsed(`/api/refurbishments/${query({ unitId, sort: "opened", dir: "desc", skip: 0, take: 50 })}`, refurbishmentWorkOrderPage, { signal }),
  detail: (id: string, signal?: AbortSignal) => apiParsed(base(id), refurbishmentWorkOrderRow, { signal }),
  addOperation: (id: string, sequence: number, description: string, plannedHours: number) => apiParsed(`${base(id)}/operations`, z.unknown(), { method: "POST", body: { commandId: newCommandId(), sequence, description, technicianRef: null, plannedHours, eventTime: now() } }),
  completeOperation: (id: string, operationId: string, actualHours: number) => apiParsed(`${base(id)}/operations/${operationId}/complete`, z.unknown(), { method: "POST", body: { commandId: newCommandId(), actualHours, completedAt: now() } }),
  removePart: (id: string, assemblyLinkId: string, destination: string, destinationLocationId: string | null, vendorPartyRef: string | null) => apiParsed(`${base(id)}/parts/remove`, z.unknown(), { method: "POST", body: { commandId: newCommandId(), assemblyLinkId, destination, eventTime: now(), destinationLocationId, vendorPartyRef } }),
  installPart: (id: string, part: { childUnitId: string | null; childItemId: string | null; quantity: number | null; relation: string; slot: string | null }, unitCost: number, currency: string, repairLocationId: string | null) => apiParsed(`${base(id)}/parts/install`, z.unknown(), { method: "POST", body: { commandId: newCommandId(), part, unitCost, currency, eventTime: now(), repairLocationId } }),
  labour: (id: string, operationId: string | null, hours: number, hourlyRate: number, currency: string) => apiParsed(`${base(id)}/labour`, z.unknown(), { method: "POST", body: { commandId: newCommandId(), operationId, hours, hourlyRate, currency, eventTime: now() } }),
  cost: (id: string, kind: string, amount: number, currency: string, description: string) => apiParsed(`${base(id)}/costs`, z.unknown(), { method: "POST", body: { commandId: newCommandId(), kind, amount, currency, description, eventTime: now() } }),
  release: (id: string, grade: string, valuationRule: string, warehouseLocationId: string) => apiParsed(`${base(id)}/release`, z.unknown(), { method: "POST", body: { commandId: newCommandId(), grade, valuationRule, releasedAt: now(), warehouseLocationId, newSerial: null, fCode: null, cCode: null } }),
  cancel: (id: string, reason: string, warehouseLocationId: string) => apiParsed(`${base(id)}/cancel`, z.unknown(), { method: "POST", body: { commandId: newCommandId(), reason, cancelledAt: now(), warehouseLocationId } }),
  policy: (signal?: AbortSignal) => apiParsed("/api/refurbishments/policy", refurbishmentPolicy, { signal }),
  savePolicy: (policy: RefurbishmentPolicy) => apiParsed("/api/refurbishments/policy", refurbishmentPolicy, { method: "PUT", body: policy }),
  sanitisation: (unitId: string, signal?: AbortSignal) => apiParsed(`/api/units/${unitId}/sanitisation`, z.array(sanitisationRow), { signal }),
  tests: (unitId: string, signal?: AbortSignal) => apiParsed(`/api/units/${unitId}/tests`, z.array(unitTestRow), { signal }),
  usage: (unitId: string, signal?: AbortSignal) => apiParsed(`/api/units/${unitId}/usage`, z.array(usageReadingRow), { signal }),
  attributes: (unitId: string, signal?: AbortSignal) => apiParsed(`/api/units/${unitId}/attributes/history`, z.array(unitAttributeHistoryRow), { signal }),
};
