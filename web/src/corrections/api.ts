import { api } from "../api/client";
import type { AssignIdentifiers, AssignIdentifiersPreview, AssignIdentifiersResult, AwaitingVendorSerialReport, CorrectionKpiReport, CorrectionKpiTargetPolicy, MergeUnits, MergeUnitsPreviewResult, MergeUnitsResult, PreviewMergeUnits, ReassignAllocation, ReassignAllocationResult, UpdateWorkbenchChoices, WorkbenchChoiceConfiguration, WorkbenchChoiceSettingsView } from "./types";

function query(values: Record<string, string | number | undefined | null>): string {
  const params = new URLSearchParams();
  for (const [key, value] of Object.entries(values)) if (value !== undefined && value !== null && value !== "") params.set(key, String(value));
  const text = params.toString();
  return text ? `?${text}` : "";
}

export const correctionsApi = {
  kpi: (from: string, to: string, breakdown: string, item?: string | null, signal?: AbortSignal) => api<CorrectionKpiReport>(`/api/corrections/kpi${query({ from, to, breakdown, item })}`, { signal }),
  kpiExportUrl: (from: string, to: string, breakdown: string, item?: string | null) => `/api/corrections/kpi/export${query({ from, to, breakdown, item })}`,
  kpiTargets: (signal?: AbortSignal) => api<CorrectionKpiTargetPolicy>("/api/corrections/kpi/targets", { signal }),
  saveKpiTargets: (body: CorrectionKpiTargetPolicy) => api<CorrectionKpiTargetPolicy>("/api/corrections/kpi/targets", { method: "PUT", body }),
  previewReassignment: (body: ReassignAllocation, signal?: AbortSignal) => api<ReassignAllocationResult>("/api/corrections/reassign-allocation/preview", { method: "POST", body, signal }),
  reassignAllocation: (body: ReassignAllocation) => api<ReassignAllocationResult>("/api/corrections/reassign-allocation", { method: "POST", body }),
  previewMerge: (body: PreviewMergeUnits, signal?: AbortSignal) => api<MergeUnitsPreviewResult>("/api/units/merge/preview", { method: "POST", body, signal }),
  merge: (body: MergeUnits) => api<MergeUnitsResult>("/api/units/merge", { method: "POST", body }),
  previewIdentifiers: (body: AssignIdentifiers, signal?: AbortSignal) => api<AssignIdentifiersPreview>("/api/corrections/assign-identifiers/preview", { method: "POST", body, signal }),
  assignIdentifiers: (body: AssignIdentifiers) => api<AssignIdentifiersResult>("/api/corrections/assign-identifiers", { method: "POST", body }),
  awaitingVendorSerial: (productionOrderId: string, signal?: AbortSignal) => api<AwaitingVendorSerialReport>(`/api/corrections/production-orders/${productionOrderId}/awaiting-vendor-serial`, { signal }),
  workbenchChoices: (signal?: AbortSignal) => api<WorkbenchChoiceSettingsView>("/api/correction-reference/workbench-choices", { signal }),
  saveWorkbenchChoices: (body: UpdateWorkbenchChoices) => api<WorkbenchChoiceConfiguration>("/api/correction-reference/workbench-choices", { method: "PUT", body }),
};
