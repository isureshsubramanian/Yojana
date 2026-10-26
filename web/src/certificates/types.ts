import * as z from "zod/mini";
import type { SelectionSource } from "../api/units.ts";

const text = z.string();
const nullableText = z.nullable(text);
const count = z.number();
const jsonObject = z.record(text, z.unknown());
export const pageSchema = <T extends z.ZodMiniType>(item: T) => z.object({ items: z.array(item), total: count, skip: count, take: count });
export type Page<T> = { items: T[]; total: number; skip: number; take: number };

export const certificateListRowSchema = z.object({
  certificateId: text, unitId: text, itemId: nullableText, primarySerial: text, documentKind: text, formType: text,
  version: count, trackingNumber: text, status: text, issuedAt: text, issuedAtDisplay: text, provenance: text,
  issuedBy: nullableText,
});
export type CertificateListRow = z.infer<typeof certificateListRowSchema>;
export const certificateSignatureSchema = z.object({ signatureId: text, printedName: text, meaning: text, signedAt: text, signedAtDisplay: text, hashSigned: text, method: text });
export type CertificateSignature = z.infer<typeof certificateSignatureSchema>;
export const certificateReprintSchema = z.object({ reprintId: text, version: count, requestedBy: text, at: text, atDisplay: text, recipient: nullableText });
export type CertificateReprint = z.infer<typeof certificateReprintSchema>;
export const brandingSnapshotSchema = z.object({ profile: text, displayName: nullableText, mark: nullableText, logoAssetId: nullableText, logoSha256: nullableText, logoMediaType: nullableText });
export type BrandingSnapshot = z.infer<typeof brandingSnapshotSchema>;
// The detail record has no issuedBy (only list rows carry it), so it is left out rather than required.
export const certificateDetailSchema = z.extend(z.omit(certificateListRowSchema, { issuedBy: true }), {
  templateVersionId: text, asBuiltRevisionId: nullableText, payload: jsonObject, payloadHash: text,
  supersedesCertificateId: nullableText, supersedeReason: nullableText, triggeringEventId: text,
  issuer: z.object({ name: text, registrationCode: nullableText, address: nullableText }),
  owner: z.nullable(z.object({ code: text, name: text, kind: nullableText, effectiveFrom: text })),
  branding: brandingSnapshotSchema, language: text, unknowns: z.array(text), evidence: z.array(text),
  signatures: z.array(certificateSignatureSchema), reprints: z.array(certificateReprintSchema),
  redaction: z.nullable(z.object({ redactedAt: text, originalPayloadHash: text, note: text })),
});
export type CertificateDetail = z.infer<typeof certificateDetailSchema>;
export const certificateVersionChainSchema = z.object({ releaseRecords: z.array(certificateListRowSchema), warrantyCertificates: z.array(certificateListRowSchema) });
export type CertificateVersionChain = z.infer<typeof certificateVersionChainSchema>;

export const certificateTemplateSummarySchema = z.object({
  templateId: text, code: text, name: text, documentKind: text, formType: text, appliesToItemClass: nullableText,
  builtIn: z.boolean(), latestVersion: count, latestStatus: text, effectiveFrom: nullableText, effectiveTo: nullableText,
  approvedBy: nullableText, approvedAt: nullableText, issuedCount: count,
});
export type CertificateTemplateSummary = z.infer<typeof certificateTemplateSummarySchema>;
export const certificateTemplateVersionSchema = z.object({
  templateVersionId: text, version: count, status: text, language: text, payloadSchemaVersion: text,
  effectiveFrom: nullableText, effectiveTo: nullableText, layoutKind: text, layoutBody: text, pageSize: text,
  orientation: text, marginTopMm: count, marginRightMm: count, marginBottomMm: count, marginLeftMm: count,
  headerBody: nullableText, footerBody: nullableText, brandingAssetId: nullableText,
  brandingAssets: z.record(text, text), staticText: z.record(text, text), fields: z.array(text), approvedBy: nullableText,
  approvedAt: nullableText, createdAt: text, createdBy: text, updatedAt: text, updatedBy: text,
});
export type CertificateTemplateVersion = z.infer<typeof certificateTemplateVersionSchema>;
export const certificateTemplateDetailSchema = z.object({
  templateId: text, code: text, name: text, documentKind: text, formType: text, appliesToItemClass: nullableText,
  builtIn: z.boolean(), versions: z.array(certificateTemplateVersionSchema),
});
export type CertificateTemplateDetail = z.infer<typeof certificateTemplateDetailSchema>;
export const fieldCatalogueEntrySchema = z.object({ path: text, type: text, repeating: z.boolean(), description: nullableText });
export type FieldCatalogueEntry = z.infer<typeof fieldCatalogueEntrySchema>;
export const templateValidationSchema = z.object({
  valid: z.boolean(), unknownPlaceholders: z.array(text), partyFieldViolations: z.array(text),
  declaredButUnusedFields: z.array(text), unknownHelpers: z.array(text), errors: z.array(text),
});
export type TemplateValidation = z.infer<typeof templateValidationSchema>;
export const tenantAssetSchema = z.object({
  assetId: text, kind: text, legalEntityId: nullableText, mediaType: text, sha256: text, width: count, height: count,
  byteLength: count, uploadedBy: text, uploadedAt: text, originalFileName: nullableText,
});
export type TenantAsset = z.infer<typeof tenantAssetSchema>;

export const labelTemplateSummarySchema = z.object({
  templateId: text, name: text, appliesTo: text, appliesToValue: text, versionCount: count,
  activeVersion: z.nullable(count), activeTemplateVersionId: nullableText, createdAt: text, createdBy: text,
  latestVersion: z.nullable(count), latestStatus: nullableText, effectiveFrom: nullableText, effectiveTo: nullableText,
  legalReview: z.boolean(), legalReviewedBy: nullableText, approvedAt: nullableText, printedCount: count,
});
export type LabelTemplateSummary = z.infer<typeof labelTemplateSummarySchema>;
export const labelBindingSchema = z.object({ placeholder: text, fieldPath: text, required: z.boolean() });
export const labelTemplateVersionSchema = z.object({
  templateVersionId: text, templateId: text, version: count, effectiveFrom: text, effectiveTo: nullableText,
  layoutKind: text, layout: text, staticText: z.array(text), bindings: z.array(labelBindingSchema), symbology: text,
  previewSampleUnitId: nullableText, legalReview: z.boolean(), legalReviewedBy: nullableText,
  legalReviewedAt: nullableText, regulated: z.boolean(), approvedBy: nullableText, approvedAt: nullableText,
  previewCurrent: z.boolean(), legalReviewCurrent: z.boolean(), approvalCurrent: z.boolean(), status: text,
  recordedAt: text, actorRef: text,
});
export type LabelTemplateVersion = z.infer<typeof labelTemplateVersionSchema>;
export const labelTemplateDetailSchema = z.object({ templateId: text, name: text, appliesTo: text, appliesToValue: text, createdAt: text, createdBy: text, versions: z.array(labelTemplateVersionSchema) });
export type LabelTemplateDetail = z.infer<typeof labelTemplateDetailSchema>;

export const certificateSelectionKpisSchema = z.object({ inSelection: count, newVersion1: count, reprintAsCopy: count, notIssuable: count, outputDocuments: count, bundle: z.boolean() });
export type CertificateSelectionKpis = z.infer<typeof certificateSelectionKpisSchema>;
export const certificateSelectionUnitSchema = z.object({
  unitId: text, serial: nullableText, itemId: nullableText, itemCode: nullableText, itemName: nullableText,
  revision: nullableText, certificateId: nullableText, certificateVersion: z.nullable(count), trackingNumber: nullableText,
  outcome: z.enum(["issue_version_1", "reprint_copy", "not_issuable"]), reason: nullableText, detail: nullableText,
  unblockingStep: nullableText,
});
export type CertificateSelectionUnit = z.infer<typeof certificateSelectionUnitSchema>;
export const certificateSelectionPreviewSchema = z.object({ selectionId: text, kpis: certificateSelectionKpisSchema, units: z.array(certificateSelectionUnitSchema), filteredTotal: count, skip: count, take: count });
export type CertificateSelectionPreview = z.infer<typeof certificateSelectionPreviewSchema>;
export const certificateOutputEntrySchema = z.object({ unitId: text, serial: text, certificateId: text, version: count, trackingNumber: text, fileName: text, mediaType: text, copy: z.boolean() });
export type CertificateOutputEntry = z.infer<typeof certificateOutputEntrySchema>;
export const certificateRunResultSchema = z.object({ commandId: text, replayed: z.boolean(), kpis: certificateSelectionKpisSchema, documents: z.array(certificateOutputEntrySchema), skipped: z.array(certificateSelectionUnitSchema), archive: text, archiveMediaType: text, archiveFileName: text });
export type CertificateRunResult = z.infer<typeof certificateRunResultSchema>;
export const certificateSelectionPreviewRequestSchema = z.object({ selectionId: text, documentKind: text, formType: text, issuerEntityId: text, language: text, includeBundle: z.boolean(), skip: count, take: count, find: nullableText, outcome: nullableText, sort: text, direction: text });
export type CertificateSelectionPreviewRequest = z.infer<typeof certificateSelectionPreviewRequestSchema>;
export const issueCertificateRequestSchema = z.object({ commandId: text, selectionId: text, documentKind: text, formType: text, issuerEntityId: text, language: text, reprintReason: text, includeBundle: z.boolean() });
export type IssueCertificateRequest = z.infer<typeof issueCertificateRequestSchema>;
export type CertificateSelectionSource = SelectionSource;

// ADR-0023: matches CertificateCorrectionPreview, ReissueCertificate(Result) and PrintCertificateCopy in
// CertificateContracts.cs. Change both sides together.
export const certificateCorrectionChangeSchema = z.object({ path: text, label: text, before: nullableText, after: nullableText, rows: count, derived: z.boolean() });
export type CertificateCorrectionChange = z.infer<typeof certificateCorrectionChangeSchema>;
export const certificateCorrectionPreviewSchema = z.object({
  certificateId: text, trackingNumber: text, fromVersion: count, toVersion: count, canCorrect: z.boolean(),
  refusalCode: nullableText, refusalDetail: nullableText, changes: z.array(certificateCorrectionChangeSchema),
});
export type CertificateCorrectionPreview = z.infer<typeof certificateCorrectionPreviewSchema>;
export const reissueCertificateRequestSchema = z.object({ commandId: text, certificateId: text, reason: text });
export const reissueCertificateResultSchema = z.object({ commandId: text, replayed: z.boolean(), supersededCertificateId: text, certificateId: text, version: count, trackingNumber: text });
export type ReissueCertificateResult = z.infer<typeof reissueCertificateResultSchema>;
export const printCertificateCopyRequestSchema = z.object({ commandId: text, certificateId: text, reason: text });
