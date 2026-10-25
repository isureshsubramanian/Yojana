import * as z from "zod/mini";
import { apiParsed, checkedBody } from "../api/client.ts";
import type { BulkOperationArguments, SelectionSource } from "../api/units.ts";
import {
  amendmentApprovalPolicySchema, bulkApprovalRequestPageSchema, bulkApprovalRequestSchema, coverageFamilySchema,
  documentRefSchema, entitlementRequirementPageSchema, entitlementRequirementSchema, entitlementViewSchema,
  gapPageSchema, gapRunSchema, policyDetailResponseSchema, policyDraftResultSchema, policySummarySchema,
  policyTermsSchema, policyValidationSchema, policyVersionSchema, refurbishedGoodsPolicySchema,
  replacementPolicySchema, tenantCurrencySchema, unitEntitlementDetailSchema, warrantyCheckSchema,
  warrantyLineExceptionPageSchema, warrantyLineExceptionSchema, warrantyReferencePolicySchema, type PolicyDetail,
} from "./types.ts";

const text = z.string();
const nullableText = z.nullable(text);
const requestDocumentRefSchema = z.object({ type: text, id: text, line: z.optional(z.nullable(z.number())) });
const selectionSourceRequestSchema = z.object({ kind: text, document: z.optional(z.nullable(requestDocumentRefSchema)), partyRef: z.optional(nullableText), itemId: z.optional(nullableText), from: z.optional(nullableText), to: z.optional(nullableText), eventType: z.optional(nullableText), serials: z.optional(z.nullable(z.array(text))), scheme: z.optional(nullableText), rangeStart: z.optional(nullableText), rangeEnd: z.optional(nullableText), importJobId: z.optional(nullableText) });
const selectionSourceResponseSchema = z.object({ kind: text, document: z.nullable(documentRefSchema), partyRef: nullableText, itemId: nullableText, from: nullableText, to: nullableText, eventType: nullableText, serials: z.nullable(z.array(text)), scheme: nullableText, rangeStart: nullableText, rangeEnd: nullableText, importJobId: nullableText });
const resolveSelectionRequestSchema = z.object({ commandId: text, source: selectionSourceRequestSchema, actorRef: text });
const resolveSelectionResultSchema = z.object({ selectionId: text, resolved: z.number(), unknown: z.array(text), rejected: z.array(text), source: selectionSourceResponseSchema });
const bulkArgumentsRequestSchema = z.object({ eventTime: text, actorRef: text, legalEntityId: z.optional(text), sourceDocument: requestDocumentRefSchema, reason: z.optional(nullableText), note: z.optional(nullableText), extra: z.optional(z.nullable(z.record(text, text))) });
const unitOutcomeSchema = z.object({ unitId: text, serial: nullableText, outcome: text, reason: nullableText, detail: nullableText, unblockingStep: nullableText });
const bulkPreviewResultSchema = z.object({ selectionId: text, operation: text, total: z.number(), change: z.number(), skip: z.number(), block: z.number(), page: z.array(unitOutcomeSchema), pageSkip: z.number(), pageTake: z.number() });
const bulkExecutionResultSchema = z.object({ commandId: text, selectionId: text, operation: text, total: z.number(), done: z.number(), skipped: z.number(), blocked: z.number(), validationReportId: text, replayed: z.boolean() });
const updatePolicyRequestSchema = z.object({ policyVersionId: text, name: text, appliesToContractsFrom: text, terms: policyTermsSchema, expectedVersion: text });
const publishPolicyRequestSchema = z.object({ policyVersionId: text });
const waiveRequirementRequestSchema = z.object({ requirementId: text, reason: text, actorRef: text });
const resolveExceptionRequestSchema = z.object({ exceptionId: text, serials: z.array(text), actorRef: text });
const previewSelectionRequestSchema = z.object({ selectionId: text, operation: text, arguments: bulkArgumentsRequestSchema, skip: z.number(), take: z.number(), find: nullableText, outcomeFilter: nullableText, approval: z.null() });
const executeSelectionRequestSchema = z.object({ commandId: text, selectionId: text, operation: text, arguments: bulkArgumentsRequestSchema, approval: z.object({ approverRef: z.null(), required: z.boolean() }) });
const raiseApprovalRequestSchema = z.object({ commandId: text, selectionId: text, operation: text, arguments: bulkArgumentsRequestSchema, note: nullableText, requester: z.object({ approverRef: z.null() }) });
const approveRequestSchema = z.object({ commandId: text, requestId: text, note: nullableText, approver: z.object({ approverRef: z.null() }) });
const rejectRequestSchema = z.object({ requestId: text, note: text, decider: z.object({ approverRef: z.null() }) });

function query(values: Record<string, string | number | boolean | undefined | null>): string {
  const result = new URLSearchParams();
  for (const [key, value] of Object.entries(values)) if (value !== undefined && value !== null && value !== "") result.set(key, String(value));
  const text = result.toString();
  return text ? `?${text}` : "";
}

export const entitlementApi = {
  forUnit: (unitId: string, signal?: AbortSignal) => apiParsed(`/api/units/${unitId}/entitlements`, z.array(entitlementViewSchema), { signal }),
  detailForUnit: (unitId: string, signal?: AbortSignal) => apiParsed(`/api/units/${unitId}/entitlements/detail`, unitEntitlementDetailSchema, { signal }),
  attachExtended: (body: unknown) => apiParsed("/api/entitlements/extended", z.unknown(), { method: "POST", body }),
  amend: (entitlementId: string, body: unknown) => apiParsed(`/api/entitlements/${entitlementId}/amend`, z.unknown(), { method: "POST", body }),
  warrantyCheck: (unitId: string, signal?: AbortSignal) => apiParsed(`/api/units/${unitId}/warranty-check`, warrantyCheckSchema, { signal }),
  policies: (family?: string, signal?: AbortSignal) => apiParsed(`/api/warranty-policies/${query({ family })}`, z.array(policySummarySchema), { signal }),
  policyVersions: (code: string, signal?: AbortSignal) => apiParsed(`/api/warranty-policies/${encodeURIComponent(code)}/versions`, z.array(policyVersionSchema), { signal }),
  policyDetail: (id: string, signal?: AbortSignal): Promise<PolicyDetail> => apiParsed(`/api/warranty-policies/versions/${id}`, policyDetailResponseSchema, { signal }),
  updatePolicy: (detail: PolicyDetail) => { const body = { policyVersionId: detail.policyVersionId, name: detail.name, appliesToContractsFrom: detail.appliesToContractsFrom, terms: detail.terms, expectedVersion: detail.documentVersion }; return apiParsed(`/api/warranty-policies/versions/${detail.policyVersionId}`, policyDraftResultSchema, { method: "PUT", body: checkedBody(updatePolicyRequestSchema, body) }); },
  validatePolicy: (id: string) => apiParsed(`/api/warranty-policies/versions/${id}/validate`, policyValidationSchema, { method: "POST", body: {} }),
  publishPolicy: (id: string) => apiParsed(`/api/warranty-policies/versions/${id}/publish`, policyDraftResultSchema, { method: "POST", body: checkedBody(publishPolicyRequestSchema, { policyVersionId: id }) }),
  createPolicyDraft: (body: unknown) => apiParsed("/api/warranty-policies/drafts", policyDraftResultSchema, { method: "POST", body }),
  families: (signal?: AbortSignal) => apiParsed("/api/warranty-reference/coverage-families", z.array(coverageFamilySchema), { signal }),
  replacementPolicies: (signal?: AbortSignal) => apiParsed("/api/warranty-reference/replacement-policies", z.array(replacementPolicySchema), { signal }),
  approvalPolicy: (signal?: AbortSignal) => apiParsed("/api/warranty-reference/amendment-approval", amendmentApprovalPolicySchema, { signal }),
  refurbishedPolicy: (signal?: AbortSignal) => apiParsed("/api/warranty-reference/refurbished-goods", refurbishedGoodsPolicySchema, { signal }),
  serviceContractRules: (signal?: AbortSignal) => apiParsed("/api/warranty-reference/service-contract-rules", warrantyReferencePolicySchema, { signal }),
  currency: (signal?: AbortSignal) => apiParsed("/api/tenant/currency", tenantCurrencySchema, { signal }),
  requirements: (status: string | null, skip: number, take: number, signal?: AbortSignal) => apiParsed(`/api/entitlements/requirements${query({ status, skip, take })}`, entitlementRequirementPageSchema, { signal }),
  waiveRequirement: (id: string, reason: string, actorRef: string) => apiParsed(`/api/entitlements/requirements/${id}/waive`, entitlementRequirementSchema, { method: "POST", body: checkedBody(waiveRequirementRequestSchema, { requirementId: id, reason, actorRef }) }),
  exceptions: (status: string | null, skip: number, take: number, signal?: AbortSignal) => apiParsed(`/api/warranty-lines/exceptions${query({ status, skip, take })}`, warrantyLineExceptionPageSchema, { signal }),
  resolveException: (id: string, serials: string[], actorRef: string) => apiParsed(`/api/warranty-lines/exceptions/${id}/resolve`, warrantyLineExceptionSchema, { method: "POST", body: checkedBody(resolveExceptionRequestSchema, { exceptionId: id, serials, actorRef }) }),
  gaps: (filters: Record<string, string | number | undefined | null>, signal?: AbortSignal) => apiParsed(`/api/entitlements/gap-report${query(filters)}`, gapPageSchema, { signal }),
  latestGapRun: (signal?: AbortSignal) => apiParsed("/api/entitlements/gap-report/runs/latest", gapRunSchema, { signal }),
  gapExportUrl: (filters: Record<string, string | number | undefined | null>) => `/api/entitlements/gap-report/export${query(filters)}`,
  resolveSelection: (source: SelectionSource, actorRef: string, commandId: string) => apiParsed("/api/selections/", resolveSelectionResultSchema, { method: "POST", body: checkedBody(resolveSelectionRequestSchema, { commandId, source, actorRef }) }),
  previewSelection: (selectionId: string, operation: string, args: BulkOperationArguments, skip: number, take: number, find: string | null, outcomeFilter: string | null, signal?: AbortSignal) => { const body = { selectionId, operation, arguments: args, skip, take, find, outcomeFilter, approval: null }; return apiParsed(`/api/selections/${selectionId}/preview`, bulkPreviewResultSchema, { method: "POST", body: checkedBody(previewSelectionRequestSchema, body), signal }); },
  executeSelection: (selectionId: string, operation: string, args: BulkOperationArguments, commandId: string) => { const body = { commandId, selectionId, operation, arguments: args, approval: { approverRef: null, required: false } }; return apiParsed(`/api/selections/${selectionId}/execute`, bulkExecutionResultSchema, { method: "POST", body: checkedBody(executeSelectionRequestSchema, body) }); },
  approvalRequests: (status: string | null, skip: number, take: number, signal?: AbortSignal) => apiParsed(`/api/approval-requests/${query({ status, skip, take })}`, bulkApprovalRequestPageSchema, { signal }),
  approvalRequest: (id: string, signal?: AbortSignal) => apiParsed(`/api/approval-requests/${id}`, bulkApprovalRequestSchema, { signal }),
  // Requester and approver are the signed-in user; the server ignores any identity in the body.
  raiseApproval: (selectionId: string, operation: string, args: BulkOperationArguments, note: string | null, commandId: string) => { const body = { commandId, selectionId, operation, arguments: args, note, requester: { approverRef: null } }; return apiParsed("/api/approval-requests/", bulkApprovalRequestSchema, { method: "POST", body: checkedBody(raiseApprovalRequestSchema, body) }); },
  approveRequest: (id: string, note: string | null, commandId: string) => { const body = { commandId, requestId: id, note, approver: { approverRef: null } }; return apiParsed(`/api/approval-requests/${id}/approve`, bulkApprovalRequestSchema, { method: "POST", body: checkedBody(approveRequestSchema, body) }); },
  rejectRequest: (id: string, note: string) => { const body = { requestId: id, note, decider: { approverRef: null } }; return apiParsed(`/api/approval-requests/${id}/reject`, bulkApprovalRequestSchema, { method: "POST", body: checkedBody(rejectRequestSchema, body) }); },
};
