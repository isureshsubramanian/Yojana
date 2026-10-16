import type { BulkOperationArguments, DocumentRef, SelectionSource, UnitOutcome } from "../api/units";

export interface EntitlementView { entitlementId: string; unitId: string; coverageFamily: string; direction: string; type: string; storedStatus: string; effectiveStatus: string; closedReason: string | null; startDate: string | null; endDate: string | null; daysRemaining: number | null; policyVersionId: string; startBasis: string | null; startTimeZone: string; parentEntitlementId: string | null; rootEntitlementId: string | null; supersedesEntitlementId: string | null }
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
export interface PolicyDetail { policyVersionId: string; policyCode: string; version: number; name: string; coverageFamily: string; entitlementType: string; appliesToContractsFrom: string; status: string; ruleVersion: string; terms: PolicyTerms; validation: PolicyValidation }
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

export interface StoredApproval { id: string; selectionId: string; operation: string; source: SelectionSource; arguments: BulkOperationArguments; blocked: UnitOutcome[]; total: number; createdAt: string }
