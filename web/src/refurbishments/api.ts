import { api } from "../api/client";
import { DEFAULT_LEGAL_ENTITY, newCommandId, now } from "../api/units";
import type {
  RefurbishmentPolicy, RefurbishmentWorkOrderPage, RefurbishmentWorkOrderRow, SanitisationRow,
  UnitAttributeHistoryRow, UnitTestRow, UsageReadingRow,
} from "./types";

function query(values: Record<string, string | number | null | undefined>): string {
  const params = new URLSearchParams();
  for (const [key, value] of Object.entries(values)) if (value !== null && value !== undefined && value !== "") params.set(key, String(value));
  const text = params.toString(); return text ? `?${text}` : "";
}

const base = (workOrderId: string) => `/api/refurbishments/${workOrderId}`;
export const refurbishmentsApi = {
  list: (status: string, kind: string, skip: number, take: number, signal?: AbortSignal) => api<RefurbishmentWorkOrderPage>(`/api/refurbishments/${query({ status, kind, skip, take })}`, { signal }),
  forUnit: (unitId: string, signal?: AbortSignal) => api<RefurbishmentWorkOrderPage>(`/api/refurbishments/${query({ unitId, skip: 0, take: 50 })}`, { signal }),
  detail: (id: string, signal?: AbortSignal) => api<RefurbishmentWorkOrderRow>(base(id), { signal }),
  addOperation: (id: string, sequence: number, description: string, plannedHours: number) => api<unknown>(`${base(id)}/operations`, { method: "POST", body: { commandId: newCommandId(), sequence, description, technicianRef: null, plannedHours, eventTime: now(), legalEntityId: DEFAULT_LEGAL_ENTITY } }),
  completeOperation: (id: string, operationId: string, actualHours: number) => api<unknown>(`${base(id)}/operations/${operationId}/complete`, { method: "POST", body: { commandId: newCommandId(), actualHours, completedAt: now(), legalEntityId: DEFAULT_LEGAL_ENTITY } }),
  removePart: (id: string, assemblyLinkId: string, destination: string, destinationLocationId: string | null, vendorPartyRef: string | null) => api<unknown>(`${base(id)}/parts/remove`, { method: "POST", body: { commandId: newCommandId(), assemblyLinkId, destination, eventTime: now(), legalEntityId: DEFAULT_LEGAL_ENTITY, destinationLocationId, vendorPartyRef } }),
  installPart: (id: string, part: { childUnitId: string | null; childItemId: string | null; quantity: number | null; relation: string; slot: string | null }, unitCost: number, currency: string, repairLocationId: string | null) => api<unknown>(`${base(id)}/parts/install`, { method: "POST", body: { commandId: newCommandId(), part, unitCost, currency, eventTime: now(), legalEntityId: DEFAULT_LEGAL_ENTITY, repairLocationId } }),
  labour: (id: string, operationId: string | null, hours: number, hourlyRate: number, currency: string) => api<unknown>(`${base(id)}/labour`, { method: "POST", body: { commandId: newCommandId(), operationId, hours, hourlyRate, currency, eventTime: now(), legalEntityId: DEFAULT_LEGAL_ENTITY } }),
  cost: (id: string, kind: string, amount: number, currency: string, description: string) => api<unknown>(`${base(id)}/costs`, { method: "POST", body: { commandId: newCommandId(), kind, amount, currency, description, eventTime: now(), legalEntityId: DEFAULT_LEGAL_ENTITY } }),
  release: (id: string, grade: string, valuationRule: string, warehouseLocationId: string) => api<unknown>(`${base(id)}/release`, { method: "POST", body: { commandId: newCommandId(), grade, valuationRule, releasedAt: now(), legalEntityId: DEFAULT_LEGAL_ENTITY, warehouseLocationId, newSerial: null, fCode: null, cCode: null } }),
  cancel: (id: string, reason: string, warehouseLocationId: string) => api<unknown>(`${base(id)}/cancel`, { method: "POST", body: { commandId: newCommandId(), reason, cancelledAt: now(), legalEntityId: DEFAULT_LEGAL_ENTITY, warehouseLocationId } }),
  policy: (signal?: AbortSignal) => api<RefurbishmentPolicy>("/api/refurbishments/policy", { signal }),
  savePolicy: (policy: RefurbishmentPolicy) => api<RefurbishmentPolicy>("/api/refurbishments/policy", { method: "PUT", body: policy }),
  sanitisation: (unitId: string, signal?: AbortSignal) => api<SanitisationRow[]>(`/api/units/${unitId}/sanitisation`, { signal }),
  tests: (unitId: string, signal?: AbortSignal) => api<UnitTestRow[]>(`/api/units/${unitId}/tests`, { signal }),
  usage: (unitId: string, signal?: AbortSignal) => api<UsageReadingRow[]>(`/api/units/${unitId}/usage`, { signal }),
  attributes: (unitId: string, signal?: AbortSignal) => api<UnitAttributeHistoryRow[]>(`/api/units/${unitId}/attributes/history`, { signal }),
};
