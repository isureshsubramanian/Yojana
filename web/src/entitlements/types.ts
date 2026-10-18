import type { BulkOperationArguments, DocumentRef } from "../api/units";

export interface EntitlementView { entitlementId: string; unitId: string; coverageFamily: string; direction: string; type: string; storedStatus: string; effectiveStatus: string; closedReason: string | null; startDate: string | null; endDate: string | null; daysRemaining: number | null; policyVersionId: string; startBasis: string | null; startTimeZone: string; parentEntitlementId: string | null; rootEntitlementId: string | null; supersedesEntitlementId: string | null }
export interface UnitEntitlementRow { entitlementId: string; coverageFamily: string; coverageFamilyName: string; accountingTag: string; direction: string; type: string; storedStatus: string; effectiveStatus: string; future: boolean; closedReason: string | null; closedEffective: string | null; ownerPartyRef: string | null; ownerPartyType: string | null; policyCode: string; policyName: string; policyVersion: number; terms: CoverageTerms; regionScope: string[]; transferable: string; sourceDocumentType: string | null; sourceDocumentId: string | null; sourceDocumentLine: number | null; accrualCohort: string | null; startDate: string | null; startBasis: string | null; startExplanation: string | null; endDate: string | null; endInclusive: boolean; computedByRuleVersion: string | null; daysRemaining: number | null; parentEntitlementId: string | null; rootEntitlementId: string | null; supersedesEntitlementId: string | null; successorEntitlementId: string | null }
export interface EntitlementLedgerEvent { eventId: string; type: string; at: string; actor: string; documentType: string | null; documentId: string | null; documentLine: number | null; attributes: string }
export interface EntitlementAmendmentRow { amendmentId: string; entitlementId: string; field: string; before: string | null; after: string | null; reasonCode: string; approvedBy: string | null; actor: string; at: string }
export interface EntitlementSuspensionRow { suspensionId: string; entitlementId: string; from: string; to: string | null; reason: string; clockPaused: boolean; actor: string }
export interface EntitlementUsageLimitRow { entitlementId: string; coverageFamily: string; meterType: string; limit: number; used: number; left: number; period: string; resetsOn: string | null }
export interface UnitEntitlementDetail { unitId: string; asOf: string; hasOpenClaims: boolean; entitlements: UnitEntitlementRow[]; lineage: EntitlementLedgerEvent[]; amendments: EntitlementAmendmentRow[]; suspensions: EntitlementSuspensionRow[]; usageLimits: EntitlementUsageLimitRow[] }
export interface WarrantyCoveringEntitlement { entitlementId: string; family: string; type: string; storedStatus: string; effectiveStatus: string; start: string | null; end: string | null; daysRemaining: number | null; coverage: string }
export interface WarrantyCheck { unitId: string; on: string; family: string | null; covered: boolean; coveringEntitlements: WarrantyCoveringEntitlement[]; endDate: string | null; daysRemaining: number | null; reason: string | null }

export interface DurationRule { value: number; unit: string; endOfMonth: string; endInclusive: boolean }
export interface StartFallback { trigger: string; offsetDays: number }
export interface StartRule { trigger: string; fallbacks: StartFallback[]; capDays: number | null }
export interface UsageLimitTerm { meterType: string; limit: number; period: string }
export interface CoverageTerms { partsPct: number; labourPct: number; travelPct: number; serviceMode: string; responseHours: number | null; coverageHours: string | null }
export interface RefurbishedTerm { itemId: string | null; grade: string; channel: string; region: string; durationMonths: number; statutoryFloorMonths: number | null; statementKind: string }
export interface EligibilityRule { kind: string; baseFamily: string; withinDays: number | null }
export interface AmendmentValueTerm { amount: number; currency: string; per: string }
export interface PolicyTerms { duration: DurationRule; durationBasis: string; usageLimits: UsageLimitTerm[]; start: StartRule; coverage: CoverageTerms; exclusions: string[]; regionScope: string[]; transferable: string; settlementFormula: string; replacementPolicyCode: string | null; refurbished: RefurbishedTerm[]; doaWindowDays: number | null; assuranceOrService: string; referenceTimeZoneRule: string; serviceContractRegulated: boolean; eligibility: EligibilityRule | null; amendmentValue: AmendmentValueTerm | null }
export interface PolicyIssue { severity: string; code: string; field: string; message: string }
export interface PolicyValidation { issues: PolicyIssue[]; canPublish: boolean }
export interface PolicySummary { policyCode: string; name: string; coverageFamily: string; versions: number; inForceVersion: number | null; inForceFrom: string | null; draftVersion: number | null; liveEntitlements: number }
export interface PolicyVersion { policyVersionId: string; version: number; appliesToContractsFrom: string; status: string; ruleVersion: string; liveEntitlements: number; createdAt: string; createdBy: string; publishedAt: string | null; publishedBy: string | null }
export interface PolicyDetail { policyVersionId: string; policyCode: string; version: number; name: string; coverageFamily: string; entitlementType: string; appliesToContractsFrom: string; status: string; ruleVersion: string; terms: PolicyTerms; validation: PolicyValidation; documentVersion: string }
export interface PolicyDraftResult { policyVersionId: string; policyCode: string; version: number; status: string; validation: PolicyValidation }
export interface CoverageFamily { familyCode: string; name: string; stacking: boolean; accountingTag: string; active: boolean }
export interface ReplacementPolicy { replacementPolicyId: string; code: string; rule: string; minDays: number | null; extensionMonths: number | null; description: string | null }
export interface AmendmentApprovalTier { upToDays: number | null; role: string; financeReview: boolean; financeReviewAboveAmount: number | null }
export interface AmendmentReasonRule { reasonCode: string; sourceDocumentRequired: boolean; systemOnly: boolean; tiers: AmendmentApprovalTier[] }
export interface AmendmentApprovalPolicy { reasons: AmendmentReasonRule[]; reductionRole: string }
export interface RefurbishedFloor { countryOrRegionGroup: string; buyerKind: string; minimumMonths: number }
export interface RefurbishedGoodsPolicy { floors: RefurbishedFloor[] }
export interface WarrantyJurisdictionRule { countryOrRegionGroup: string; allowed: boolean; disclosureRequired: boolean; licensedObligorRequired: boolean }
export interface WarrantyReferencePolicy { jurisdictions: WarrantyJurisdictionRule[] }
export interface TenantCurrency { currency: string | null }

export interface EntitlementRequirement { requirementId: string; unitId: string; ownershipEpisodeId: string; itemId: string; sourceDocument: DocumentRef | null; status: string; missingInput: string | null; detail: string | null; raisedAt: string; resolvedAt: string | null; entitlementId: string | null }
export interface EntitlementRequirementPage { items: EntitlementRequirement[]; total: number; skip: number; take: number }
export interface WarrantyLineException { exceptionId: string; document: DocumentRef; itemId: string; status: string; missingInput: string; detail: string | null; triggerAt: string; raisedAt: string; resolvedAt: string | null }
export interface WarrantyLineExceptionPage { items: WarrantyLineException[]; total: number; skip: number; take: number }
export interface GapRow { unitId: string; primarySerial: string | null; itemId: string; saleDate: string; customerPartyRef: string; channel: string; dropShip: boolean; requirementStatus: string | null; missingInput: string | null }
export interface GapPage { rows: GapRow[]; total: number; skip: number; take: number }
export interface GapCount { itemId: string; channel: string; dropShip: boolean; count: number }
export interface GapRun { runId: string; runAt: string; trigger: string; total: number; counts: GapCount[] }

/** P2-T21: a bulk execution waiting for approval, stored by the server with a fingerprint of the requester's preview. */
export interface BulkApprovalRequest { requestId: string; selectionId: string; operation: string; status: "pending" | "approved" | "rejected" | "withdrawn" | "stale"; sourceDocument: DocumentRef; arguments: BulkOperationArguments; total: number; change: number; skip: number; block: number; rules: string[]; requestedBy: string; requestedAt: string; note: string | null; decidedBy: string | null; decidedAt: string | null; decisionNote: string | null; executedCommandId: string | null }
export interface BulkApprovalRequestPage { items: BulkApprovalRequest[]; total: number; skip: number; take: number }
