import type { SelectionSource } from "../api/units";

export interface CertificateSelectionKpis { inSelection: number; newVersion1: number; reprintAsCopy: number; notIssuable: number; outputDocuments: number; bundle: boolean }
export interface CertificateSelectionUnit { unitId: string; serial: string | null; itemId: string | null; itemCode: string | null; itemName: string | null; revision: string | null; certificateId: string | null; certificateVersion: number | null; trackingNumber: string | null; outcome: "issue_version_1" | "reprint_copy" | "not_issuable"; reason: string | null; detail: string | null; unblockingStep: string | null }
export interface CertificateSelectionPreview { selectionId: string; kpis: CertificateSelectionKpis; units: CertificateSelectionUnit[]; filteredTotal: number; skip: number; take: number }
export interface CertificateOutputEntry { unitId: string; serial: string; certificateId: string; version: number; trackingNumber: string; fileName: string; mediaType: string; copy: boolean }
export interface CertificateRunResult { commandId: string; replayed: boolean; kpis: CertificateSelectionKpis; documents: CertificateOutputEntry[]; skipped: CertificateSelectionUnit[]; archive: string; archiveMediaType: string; archiveFileName: string }
export interface CertificateSelectionPreviewRequest { selectionId: string; documentKind: string; formType: string; issuerEntityId: string; language: string; includeBundle: boolean; skip: number; take: number; find: string | null; outcome: string | null }
export interface IssueCertificateRequest { commandId: string; selectionId: string; documentKind: string; formType: string; issuerEntityId: string; language: string; reprintReason: string; includeBundle: boolean }
export type CertificateSelectionSource = SelectionSource;
