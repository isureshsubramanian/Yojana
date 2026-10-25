import * as z from "zod/mini";
import { apiParsed } from "../api/client.ts";
import {
  assignIdentifiersRequest,
  assignIdentifiersPreview,
  assignIdentifiersResult,
  awaitingVendorSerialReport,
  correctionKpiReport,
  correctionKpiTargetPolicy,
  correctionKpiTargetPolicyRequest,
  mergeUnitsRequest,
  mergeUnitsPreviewResult,
  mergeUnitsResult,
  previewMergeUnitsRequest,
  reassignAllocationRequest,
  reassignAllocationResult,
  updateWorkbenchChoicesRequest,
  workbenchChoiceConfiguration,
  workbenchChoiceSettingsView,
} from "./types.ts";
import type { AssignIdentifiers, AssignIdentifiersPreview, AssignIdentifiersResult, AwaitingVendorSerialReport, CorrectionKpiReport, CorrectionKpiTargetPolicy, MergeUnits, MergeUnitsPreviewResult, MergeUnitsResult, PreviewMergeUnits, ReassignAllocation, ReassignAllocationResult, UpdateWorkbenchChoices, WorkbenchChoiceConfiguration, WorkbenchChoiceSettingsView } from "./types.ts";

function query(values: Record<string, string | number | undefined | null>): string {
  const params = new URLSearchParams();
  for (const [key, value] of Object.entries(values)) if (value !== undefined && value !== null && value !== "") params.set(key, String(value));
  const text = params.toString();
  return text ? `?${text}` : "";
}

export const correctionsApi = {
  kpi: (from: string, to: string, breakdown: string, item?: string | null, signal?: AbortSignal): Promise<CorrectionKpiReport> => apiParsed(`/api/corrections/kpi${query({ from, to, breakdown, item })}`, correctionKpiReport, { signal }),
  kpiExportUrl: (from: string, to: string, breakdown: string, item?: string | null) => `/api/corrections/kpi/export${query({ from, to, breakdown, item })}`,
  kpiTargets: (signal?: AbortSignal): Promise<CorrectionKpiTargetPolicy> => apiParsed("/api/corrections/kpi/targets", correctionKpiTargetPolicy, { signal }),
  saveKpiTargets: (body: CorrectionKpiTargetPolicy): Promise<CorrectionKpiTargetPolicy> => apiParsed("/api/corrections/kpi/targets", correctionKpiTargetPolicy, { method: "PUT", body: z.parse(correctionKpiTargetPolicyRequest, body) }),
  previewReassignment: (body: ReassignAllocation, signal?: AbortSignal): Promise<ReassignAllocationResult> => apiParsed("/api/corrections/reassign-allocation/preview", reassignAllocationResult, { method: "POST", body: z.parse(reassignAllocationRequest, body), signal }),
  reassignAllocation: (body: ReassignAllocation): Promise<ReassignAllocationResult> => apiParsed("/api/corrections/reassign-allocation", reassignAllocationResult, { method: "POST", body: z.parse(reassignAllocationRequest, body) }),
  previewMerge: (body: PreviewMergeUnits, signal?: AbortSignal): Promise<MergeUnitsPreviewResult> => apiParsed("/api/units/merge/preview", mergeUnitsPreviewResult, { method: "POST", body: z.parse(previewMergeUnitsRequest, body), signal }),
  merge: (body: MergeUnits): Promise<MergeUnitsResult> => apiParsed("/api/units/merge", mergeUnitsResult, { method: "POST", body: z.parse(mergeUnitsRequest, body) }),
  previewIdentifiers: (body: AssignIdentifiers, signal?: AbortSignal): Promise<AssignIdentifiersPreview> => apiParsed("/api/corrections/assign-identifiers/preview", assignIdentifiersPreview, { method: "POST", body: z.parse(assignIdentifiersRequest, body), signal }),
  assignIdentifiers: (body: AssignIdentifiers): Promise<AssignIdentifiersResult> => apiParsed("/api/corrections/assign-identifiers", assignIdentifiersResult, { method: "POST", body: z.parse(assignIdentifiersRequest, body) }),
  awaitingVendorSerial: (productionOrderId: string, signal?: AbortSignal): Promise<AwaitingVendorSerialReport> => apiParsed(`/api/corrections/production-orders/${productionOrderId}/awaiting-vendor-serial`, awaitingVendorSerialReport, { signal }),
  workbenchChoices: (signal?: AbortSignal): Promise<WorkbenchChoiceSettingsView> => apiParsed("/api/correction-reference/workbench-choices", workbenchChoiceSettingsView, { signal }),
  saveWorkbenchChoices: (body: UpdateWorkbenchChoices): Promise<WorkbenchChoiceConfiguration> => apiParsed("/api/correction-reference/workbench-choices", workbenchChoiceConfiguration, { method: "PUT", body: z.parse(updateWorkbenchChoicesRequest, body) }),
};
