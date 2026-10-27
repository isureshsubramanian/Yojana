import * as z from "zod/mini";

const text = z.string();
const nullableText = z.nullable(text);
const count = z.number();
export const labelBindingSchema = z.object({ placeholder: text, fieldPath: text, required: z.boolean() });
export type LabelBinding = z.infer<typeof labelBindingSchema>;
export const labelResolvedBindingSchema = z.extend(labelBindingSchema, { value: nullableText });
export type LabelResolvedBinding = z.infer<typeof labelResolvedBindingSchema>;

export const labelTemplateVersionSchema = z.object({
  templateVersionId: text, templateId: text, version: count, effectiveFrom: text, effectiveTo: nullableText,
  layoutKind: z.enum(["zpl", "html"]), layout: text, staticText: z.array(text), bindings: z.array(labelBindingSchema),
  symbology: z.enum(["code128", "datamatrix", "qr", "code39"]), previewSampleUnitId: nullableText,
  legalReview: z.boolean(), legalReviewedBy: nullableText, legalReviewedAt: nullableText, regulated: z.boolean(),
  approvedBy: nullableText, approvedAt: nullableText, previewCurrent: z.boolean(), legalReviewCurrent: z.boolean(),
  approvalCurrent: z.boolean(), status: z.enum(["draft", "active", "retired"]), recordedAt: text, actorRef: text,
  widthMm: z.nullable(count), heightMm: z.nullable(count), dpiHint: z.nullable(count),
});
export type LabelTemplateVersion = z.infer<typeof labelTemplateVersionSchema>;
export const labelTemplateDetailSchema = z.object({ templateId: text, name: text, appliesTo: z.enum(["item_family", "identifier_type"]), appliesToValue: text, createdAt: text, createdBy: text, versions: z.array(labelTemplateVersionSchema) });
export type LabelTemplateDetail = z.infer<typeof labelTemplateDetailSchema>;
export const labelTemplateCatalogueRowSchema = z.object({
  templateId: text, name: text, appliesTo: text, appliesToValue: text, versionCount: count,
  activeVersion: z.nullable(count), activeTemplateVersionId: nullableText, createdAt: text, createdBy: text,
  latestVersion: z.nullable(count), latestStatus: nullableText, effectiveFrom: nullableText, effectiveTo: nullableText,
  legalReview: z.boolean(), legalReviewedBy: nullableText, approvedAt: nullableText, printedCount: count,
  activeWidthMm: z.nullable(count), activeHeightMm: z.nullable(count),
});
export type LabelTemplateCatalogueRow = z.infer<typeof labelTemplateCatalogueRowSchema>;
export const labelBarcodeSchema = z.object({ symbology: text, mediaType: text, dataUrl: text, width: count, height: count, quietZone: count, content: text });
export type LabelBarcode = z.infer<typeof labelBarcodeSchema>;
export const labelTemplatePreviewSchema = z.object({
  templateVersionId: text, unitId: text, layoutKind: text, renderedPayload: nullableText,
  bindings: z.array(labelResolvedBindingSchema), emptyRequiredField: nullableText, rejectionReason: nullableText,
  barcode: z.nullable(labelBarcodeSchema),
  widthMm: z.nullable(count), heightMm: z.nullable(count),
});
export type LabelTemplatePreview = z.infer<typeof labelTemplatePreviewSchema>;
export const labelBindableFieldSchema = z.object({ fieldPath: text, expiryControlled: z.boolean() });
export type LabelBindableField = z.infer<typeof labelBindableFieldSchema>;
export const labelSheetProfileSchema = z.object({
  profileId: text, name: text, pageSize: z.enum(["A4", "Letter"]), columns: count, rows: count,
  labelWidthMm: count, labelHeightMm: count, marginTopMm: count, marginRightMm: count,
  marginBottomMm: count, marginLeftMm: count, horizontalGapMm: count, verticalGapMm: count, builtIn: z.boolean(),
});
export type LabelSheetProfile = z.infer<typeof labelSheetProfileSchema>;
export const labelPolicySchema = z.object({
  legalReviewPhrases: z.array(text), printExpiryDates: z.boolean(), defaultPrinterRef: text,
  printerAdapter: z.enum(["pdf_download", "unavailable"]), defaultFormat: z.enum(["sheet", "one_per_page"]),
  defaultSheet: labelSheetProfileSchema, extraSheetProfiles: z.array(labelSheetProfileSchema),
});
export type LabelPolicy = z.infer<typeof labelPolicySchema>;
export const labelPrintOptionsSchema = z.object({
  defaultFormat: z.enum(["sheet", "one_per_page"]), sheetProfiles: z.array(labelSheetProfileSchema), defaultSheetProfileId: text,
});
export type LabelPrintOptions = z.infer<typeof labelPrintOptionsSchema>;
export const labelPrintLayoutSchema = z.object({
  format: z.enum(["sheet", "one_per_page"]), sheetProfileId: nullableText, startRow: count, startColumn: count, copies: count,
});
export type LabelPrintLayout = z.infer<typeof labelPrintLayoutSchema>;
export const labelPrintJobSchema = z.object({
  jobId: text, jobNumber: text, templateVersionId: nullableText, unitId: nullableText, identifierId: nullableText,
  originalJobId: nullableText, templateName: nullableText, templateVersion: z.nullable(count), serial: nullableText,
  renderedPayload: nullableText, payloadHash: nullableText, printerRef: nullableText, requestedBy: text, requestedAt: text,
  status: z.enum(["rendered", "sent", "confirmed", "downloaded", "rejected"]), rejectionRule: nullableText,
  rejectionReason: nullableText, emptyRequiredField: nullableText, sentAt: nullableText, confirmedAt: nullableText,
  sourceEventId: nullableText,
  downloadedAt: nullableText, outputFormat: nullableText, sheetProfileId: nullableText,
  startRow: z.nullable(count), startColumn: z.nullable(count), copies: count, reprintReason: nullableText,
});
export type LabelPrintJob = z.infer<typeof labelPrintJobSchema>;
export const labelPrintJobPageSchema = z.object({ rows: z.array(labelPrintJobSchema), total: count, skip: count, take: count });
export type LabelPrintJobPage = z.infer<typeof labelPrintJobPageSchema>;
export const labelPrintResultSchema = z.object({ jobId: text, jobNumber: text, status: text, rejectionReason: nullableText, payloadHash: nullableText, replayed: z.boolean(), rejectionRule: nullableText, emptyRequiredField: nullableText });
export type LabelPrintResult = z.infer<typeof labelPrintResultSchema>;
export const labelPrintJobSummarySchema = z.object({ printed: count, confirmed: count, downloaded: count, waiting: count, rejected: count, reprints: count, fromEvents: count, from: text, to: text });
export type LabelPrintJobSummary = z.infer<typeof labelPrintJobSummarySchema>;
export const labelBatchPrintOutcomeSchema = z.object({ serial: text, unitId: nullableText, jobId: nullableText, status: text, rejectionRule: nullableText, rejectionReason: nullableText, emptyRequiredField: nullableText });
export type LabelBatchPrintOutcome = z.infer<typeof labelBatchPrintOutcomeSchema>;
export const labelBatchPrintResultSchema = z.object({ commandId: text, outcomes: z.array(labelBatchPrintOutcomeSchema), replayed: z.boolean() });
export type LabelBatchPrintResult = z.infer<typeof labelBatchPrintResultSchema>;
