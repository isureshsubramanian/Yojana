import type { SelectionSource } from "../api/units";

export interface Page<T> { items: T[]; total: number; skip: number; take: number }

export interface CertificateListRow {
  certificateId: string; unitId: string; itemId: string | null; primarySerial: string; documentKind: string; formType: string;
  version: number; trackingNumber: string; status: string; issuedAt: string; issuedAtDisplay: string; provenance: string;
}
export interface CertificateSignature {
  signatureId: string; printedName: string; meaning: string; signedAt: string; signedAtDisplay: string; hashSigned: string; method: string;
}
export interface CertificateReprint {
  reprintId: string; version: number; requestedBy: string; at: string; atDisplay: string; recipient: string | null;
}
export interface BrandingSnapshot {
  profile: string; displayName: string | null; mark: string | null; logoAssetId: string | null;
  logoSha256: string | null; logoMediaType: string | null;
}
export interface CertificateDetail extends CertificateListRow {
  templateVersionId: string; asBuiltRevisionId: string | null; payload: Record<string, unknown>; payloadHash: string;
  supersedesCertificateId: string | null; supersedeReason: string | null; triggeringEventId: string;
  issuer: { name: string; code: string | null }; owner: { code: string; name: string; kind: string; effectiveFrom: string } | null;
  branding: BrandingSnapshot; language: string; unknowns: string[]; evidence: string[];
  signatures: CertificateSignature[]; reprints: CertificateReprint[];
}
export interface CertificateVersionChain { releaseRecords: CertificateListRow[]; warrantyCertificates: CertificateListRow[] }

export interface CertificateTemplateSummary {
  templateId: string; code: string; name: string; documentKind: string; formType: string; appliesToItemClass: string | null;
  builtIn: boolean; latestVersion: number; latestStatus: string; effectiveFrom: string | null; effectiveTo: string | null;
  approvedBy: string | null; approvedAt: string | null; issuedCount: number;
}
export interface CertificateTemplateVersion {
  templateVersionId: string; version: number; status: string; language: string; payloadSchemaVersion: string;
  effectiveFrom: string | null; effectiveTo: string | null; layoutKind: string; layoutBody: string; pageSize: string;
  orientation: string; marginTopMm: number; marginRightMm: number; marginBottomMm: number; marginLeftMm: number;
  headerBody: string | null; footerBody: string | null; brandingAssetId: string | null;
  brandingAssets: Record<string, string>;
  staticText: Record<string, string>; fields: string[]; approvedBy: string | null; approvedAt: string | null;
  createdAt: string; createdBy: string; updatedAt: string; updatedBy: string;
}
export interface CertificateTemplateDetail {
  templateId: string; code: string; name: string; documentKind: string; formType: string; appliesToItemClass: string | null;
  builtIn: boolean; versions: CertificateTemplateVersion[];
}
export interface FieldCatalogueEntry { path: string; type: string; repeating: boolean; description: string | null }
export interface TemplateValidation {
  valid: boolean; unknownPlaceholders: string[]; partyFieldViolations: string[]; declaredButUnusedFields: string[];
  unknownHelpers: string[]; errors: string[];
}
export interface TenantAsset {
  assetId: string; kind: string; legalEntityId: string | null; mediaType: string; sha256: string;
  width: number; height: number; byteLength: number; uploadedBy: string; uploadedAt: string;
}
export interface LabelTemplateSummary {
  templateId: string; name: string; appliesTo: string; appliesToValue: string; versionCount: number; activeVersion: number | null;
  createdAt: string; createdBy: string; latestVersion: number | null; latestStatus: string | null; effectiveFrom: string | null;
  effectiveTo: string | null; legalReview: boolean; legalReviewedBy: string | null; approvedAt: string | null; printedCount: number;
}
export interface LabelTemplateVersion {
  templateVersionId: string; version: number; effectiveFrom: string; effectiveTo: string | null; layoutKind: string;
  symbology: string; legalReview: boolean; legalReviewedBy: string | null; legalReviewedAt: string | null;
  regulated: boolean; approvedBy: string | null; approvedAt: string | null; status: string;
}
export interface LabelTemplateDetail {
  templateId: string; name: string; appliesTo: string; appliesToValue: string; createdAt: string; createdBy: string;
  versions: LabelTemplateVersion[];
}

export interface CertificateSelectionKpis { inSelection: number; newVersion1: number; reprintAsCopy: number; notIssuable: number; outputDocuments: number; bundle: boolean }
export interface CertificateSelectionUnit { unitId: string; serial: string | null; itemId: string | null; itemCode: string | null; itemName: string | null; revision: string | null; certificateId: string | null; certificateVersion: number | null; trackingNumber: string | null; outcome: "issue_version_1" | "reprint_copy" | "not_issuable"; reason: string | null; detail: string | null; unblockingStep: string | null }
export interface CertificateSelectionPreview { selectionId: string; kpis: CertificateSelectionKpis; units: CertificateSelectionUnit[]; filteredTotal: number; skip: number; take: number }
export interface CertificateOutputEntry { unitId: string; serial: string; certificateId: string; version: number; trackingNumber: string; fileName: string; mediaType: string; copy: boolean }
export interface CertificateRunResult { commandId: string; replayed: boolean; kpis: CertificateSelectionKpis; documents: CertificateOutputEntry[]; skipped: CertificateSelectionUnit[]; archive: string; archiveMediaType: string; archiveFileName: string }
export interface CertificateSelectionPreviewRequest { selectionId: string; documentKind: string; formType: string; issuerEntityId: string; language: string; includeBundle: boolean; skip: number; take: number; find: string | null; outcome: string | null }
export interface IssueCertificateRequest { commandId: string; selectionId: string; documentKind: string; formType: string; issuerEntityId: string; language: string; reprintReason: string; includeBundle: boolean }
export type CertificateSelectionSource = SelectionSource;
