export interface LabelBinding { placeholder: string; fieldPath: string; required: boolean }

export interface LabelResolvedBinding extends LabelBinding { value: string | null }

export interface LabelTemplateVersion {
  templateVersionId: string;
  templateId: string;
  version: number;
  effectiveFrom: string;
  effectiveTo: string | null;
  layoutKind: "zpl" | "html";
  layout: string;
  staticText: string[];
  bindings: LabelBinding[];
  symbology: "code128" | "datamatrix" | "qr" | "code39";
  previewSampleUnitId: string | null;
  legalReview: boolean;
  legalReviewedBy: string | null;
  legalReviewedAt: string | null;
  regulated: boolean;
  approvedBy: string | null;
  approvedAt: string | null;
  status: "draft" | "active" | "retired";
  recordedAt: string;
  actorRef: string;
}

export interface LabelTemplateDetail {
  templateId: string;
  name: string;
  appliesTo: "item_family" | "identifier_type";
  appliesToValue: string;
  createdAt: string;
  createdBy: string;
  versions: LabelTemplateVersion[];
}

export interface LabelTemplateCatalogueRow {
  templateId: string;
  name: string;
  appliesTo: string;
  appliesToValue: string;
  versionCount: number;
  activeVersion: number | null;
  activeTemplateVersionId: string | null;
  createdAt: string;
  createdBy: string;
}

export interface LabelTemplatePreview {
  templateVersionId: string;
  unitId: string;
  layoutKind: string;
  renderedPayload: string | null;
  bindings: LabelResolvedBinding[];
  emptyRequiredField: string | null;
  rejectionReason: string | null;
}

export interface LabelBindableField { fieldPath: string; expiryControlled: boolean }

export interface LabelPolicy { legalReviewPhrases: string[]; printExpiryDates: boolean; defaultPrinterRef: string }

export interface LabelPrintJob {
  jobId: string;
  templateVersionId: string | null;
  unitId: string | null;
  identifierId: string | null;
  originalJobId: string | null;
  templateName: string | null;
  templateVersion: number | null;
  serial: string | null;
  renderedPayload: string | null;
  payloadHash: string | null;
  printerRef: string | null;
  requestedBy: string;
  requestedAt: string;
  status: "rendered" | "sent" | "confirmed" | "rejected";
  rejectionRule: string | null;
  rejectionReason: string | null;
  emptyRequiredField: string | null;
  sentAt: string | null;
  confirmedAt: string | null;
  sourceEventId: string | null;
}

export interface LabelPrintJobPage { rows: LabelPrintJob[]; total: number; skip: number; take: number }

export interface LabelPrintResult { jobId: string; status: string; rejectionReason: string | null; payloadHash: string | null; replayed: boolean; rejectionRule: string | null; emptyRequiredField: string | null }

export interface LabelPrintJobSummary { printed: number; confirmed: number; waiting: number; rejected: number; reprints: number; fromEvents: number; from: string; to: string }

export interface LabelBatchPrintOutcome { serial: string; unitId: string | null; jobId: string | null; status: string; rejectionRule: string | null; rejectionReason: string | null; emptyRequiredField: string | null }

export interface LabelBatchPrintResult { commandId: string; outcomes: LabelBatchPrintOutcome[]; replayed: boolean }
