import { api } from "../api/client";
import type { BulkExecutionResult, BulkOperationArguments, BulkPreviewResult, ResolveSelectionSetResult, SelectionSource } from "../api/units";
import type { AmendmentApprovalPolicy, BulkApprovalRequest, BulkApprovalRequestPage, CoverageFamily, EntitlementRequirement, EntitlementRequirementPage, EntitlementView, GapPage, GapRun, PolicyDetail, PolicyDraftResult, PolicySummary, PolicyValidation, PolicyVersion, RefurbishedGoodsPolicy, ReplacementPolicy, TenantCurrency, UnitEntitlementDetail, WarrantyCheck, WarrantyLineException, WarrantyLineExceptionPage, WarrantyReferencePolicy } from "./types";

function query(values: Record<string, string | number | boolean | undefined | null>): string {
  const result = new URLSearchParams();
  for (const [key, value] of Object.entries(values)) if (value !== undefined && value !== null && value !== "") result.set(key, String(value));
  const text = result.toString();
  return text ? `?${text}` : "";
}

export const entitlementApi = {
  forUnit: (unitId: string, signal?: AbortSignal) => api<EntitlementView[]>(`/api/units/${unitId}/entitlements`, { signal }),
  detailForUnit: (unitId: string, signal?: AbortSignal) => api<UnitEntitlementDetail>(`/api/units/${unitId}/entitlements/detail`, { signal }),
  attachExtended: (body: unknown) => api<unknown>("/api/entitlements/extended", { method: "POST", body }),
  amend: (entitlementId: string, body: unknown) => api<unknown>(`/api/entitlements/${entitlementId}/amend`, { method: "POST", body }),
  warrantyCheck: (unitId: string, signal?: AbortSignal) => api<WarrantyCheck>(`/api/units/${unitId}/warranty-check`, { signal }),
  policies: (family?: string, signal?: AbortSignal) => api<PolicySummary[]>(`/api/warranty-policies/${query({ family })}`, { signal }),
  policyVersions: (code: string, signal?: AbortSignal) => api<PolicyVersion[]>(`/api/warranty-policies/${encodeURIComponent(code)}/versions`, { signal }),
  policyDetail: (id: string, signal?: AbortSignal) => api<PolicyDetail>(`/api/warranty-policies/versions/${id}`, { signal }),
  updatePolicy: (detail: PolicyDetail) => api<PolicyDraftResult>(`/api/warranty-policies/versions/${detail.policyVersionId}`, { method: "PUT", body: { policyVersionId: detail.policyVersionId, name: detail.name, appliesToContractsFrom: detail.appliesToContractsFrom, terms: detail.terms, expectedVersion: detail.documentVersion } }),
  validatePolicy: (id: string) => api<PolicyValidation>(`/api/warranty-policies/versions/${id}/validate`, { method: "POST", body: {} }),
  publishPolicy: (id: string) => api<PolicyDraftResult>(`/api/warranty-policies/versions/${id}/publish`, { method: "POST", body: { policyVersionId: id } }),
  createPolicyDraft: (body: unknown) => api<PolicyDraftResult>("/api/warranty-policies/drafts", { method: "POST", body }),
  families: (signal?: AbortSignal) => api<CoverageFamily[]>("/api/warranty-reference/coverage-families", { signal }),
  replacementPolicies: (signal?: AbortSignal) => api<ReplacementPolicy[]>("/api/warranty-reference/replacement-policies", { signal }),
  approvalPolicy: (signal?: AbortSignal) => api<AmendmentApprovalPolicy>("/api/warranty-reference/amendment-approval", { signal }),
  refurbishedPolicy: (signal?: AbortSignal) => api<RefurbishedGoodsPolicy>("/api/warranty-reference/refurbished-goods", { signal }),
  serviceContractRules: (signal?: AbortSignal) => api<WarrantyReferencePolicy>("/api/warranty-reference/service-contract-rules", { signal }),
  currency: (signal?: AbortSignal) => api<TenantCurrency>("/api/tenant/currency", { signal }),
  requirements: (status: string | null, skip: number, take: number, signal?: AbortSignal) => api<EntitlementRequirementPage>(`/api/entitlements/requirements${query({ status, skip, take })}`, { signal }),
  waiveRequirement: (id: string, reason: string, actorRef: string) => api<EntitlementRequirement>(`/api/entitlements/requirements/${id}/waive`, { method: "POST", body: { requirementId: id, reason, actorRef } }),
  exceptions: (status: string | null, skip: number, take: number, signal?: AbortSignal) => api<WarrantyLineExceptionPage>(`/api/warranty-lines/exceptions${query({ status, skip, take })}`, { signal }),
  resolveException: (id: string, serials: string[], actorRef: string) => api<WarrantyLineException>(`/api/warranty-lines/exceptions/${id}/resolve`, { method: "POST", body: { exceptionId: id, serials, actorRef } }),
  gaps: (filters: Record<string, string | number | undefined | null>, signal?: AbortSignal) => api<GapPage>(`/api/entitlements/gap-report${query(filters)}`, { signal }),
  latestGapRun: (signal?: AbortSignal) => api<GapRun>("/api/entitlements/gap-report/runs/latest", { signal }),
  gapExportUrl: (filters: Record<string, string | number | undefined | null>) => `/api/entitlements/gap-report/export${query(filters)}`,
  resolveSelection: (source: SelectionSource, actorRef: string, commandId: string) => api<ResolveSelectionSetResult>("/api/selections/", { method: "POST", body: { commandId, source, actorRef } }),
  previewSelection: (selectionId: string, operation: string, args: BulkOperationArguments, skip: number, take: number, find: string | null, outcomeFilter: string | null, signal?: AbortSignal) => api<BulkPreviewResult>(`/api/selections/${selectionId}/preview`, { method: "POST", body: { selectionId, operation, arguments: args, skip, take, find, outcomeFilter, approval: null }, signal }),
  executeSelection: (selectionId: string, operation: string, args: BulkOperationArguments, commandId: string) => api<BulkExecutionResult>(`/api/selections/${selectionId}/execute`, { method: "POST", body: { commandId, selectionId, operation, arguments: args, approval: { approverRef: null, required: false } } }),
  approvalRequests: (status: string | null, skip: number, take: number, signal?: AbortSignal) => api<BulkApprovalRequestPage>(`/api/approval-requests/${query({ status, skip, take })}`, { signal }),
  approvalRequest: (id: string, signal?: AbortSignal) => api<BulkApprovalRequest>(`/api/approval-requests/${id}`, { signal }),
  // Requester and approver are the signed-in user; the server ignores any identity in the body.
  raiseApproval: (selectionId: string, operation: string, args: BulkOperationArguments, note: string | null, commandId: string) => api<BulkApprovalRequest>("/api/approval-requests/", { method: "POST", body: { commandId, selectionId, operation, arguments: args, note, requester: { approverRef: null } } }),
  approveRequest: (id: string, note: string | null, commandId: string) => api<BulkApprovalRequest>(`/api/approval-requests/${id}/approve`, { method: "POST", body: { commandId, requestId: id, note, approver: { approverRef: null } } }),
  rejectRequest: (id: string, note: string) => api<BulkApprovalRequest>(`/api/approval-requests/${id}/reject`, { method: "POST", body: { requestId: id, note, decider: { approverRef: null } } }),
};
