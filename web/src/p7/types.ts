export interface DocumentRef { type: string; id: string; line?: number | null }

export interface ImportRow {
  rowResultId: string; rowNumber: number; sheet: string | null; rawValue: string | null;
  normalisedValue: string | null; outcome: string; rule: string | null; orderNumber: string | null;
  lot: string | null; customerAssetTag: string | null; unitId: string | null;
  comparisonPartition: string | null; proposedAction: string | null; reviewedAction: string | null;
  collisionUnitName: string | null; collisionOwnerName: string | null;
}
export interface ImportSummary { jobId: string; source: string; fileName: string; format: string; mode: string; status: string; applied: number; skipped: number; rejected: number; recordedTime: string }
export interface ImportPage { items: ImportSummary[]; nextCursor: string | null }
export interface ImportFileHistoryRow { jobId: string; fileName: string; format: string; size: number; originalHash: string; convertedHash: string | null; uploaderName: string; uploaderReference: string; uploadedAt: string; source: string; status: string; applied: number; skipped: number; rejected: number; storageState: string; originalAvailable: boolean; convertedAvailable: boolean; archiveStoreName: string | null; mayDownload: boolean }
export interface ImportFileHistoryPage { items: ImportFileHistoryRow[]; nextCursor: string | null }
export interface FeedMessageHistoryRow { feedMessageId: string; messageReference: string; documentType: string; documentId: string; documentLine: number; importJobId: string; payloadHash: string; status: string; shipped: number; assetTags: number; rejected: number; callerName: string; receivedAt: string }
export interface FeedMessageHistoryPage { items: FeedMessageHistoryRow[]; nextCursor: string | null }
export interface ComparisonInputFile { id: string | null; name: string; hash: string | null }
export interface ComparisonHistoryRow { jobId: string; firstFile: ComparisonInputFile; secondFile: ComparisonInputFile; status: string; applied: number; skipped: number; rejected: number; ranAt: string }
export interface ComparisonHistoryPage { items: ComparisonHistoryRow[]; nextCursor: string | null }
export interface ImportRowPage { items: ImportRow[]; nextCursor: string | null }
export interface ImportJob extends ImportSummary { originalHash: string; convertedHash: string | null; target: DocumentRef | null; targetName: string | null; originalArchiveReference: string | null; convertedArchiveReference: string | null; inboxReference: string | null; rows: ImportRow[] }
export interface ImportRunResult { jobs: ImportJob[]; replayed: boolean }
export interface CompareResult { comparisonJobId: string; onlyInA: number; onlyInB: number; both: number; duplicates: number; existingCollisions: number; rows: ImportRow[] }

export interface Coverage { coverageFamily: string; start: string | null; expiry: string | null; status: string }
export interface IdentifierLineage { value: string; type: string; current: boolean; replacedBy: string | null }
export interface PortalUnit { unitId: string; serial: string; lifecycleStatus: string; itemCode: string; itemName: string | null; condition: string | null; coverage: Coverage[]; identifiers: IdentifierLineage[] }
export interface PortalLookup { outcome: string; units: PortalUnit[] }
export interface PortalContext { organisationName: string }
export interface PublicLookup { outcome: string; identifier: string | null; identifierType: string | null; itemCode: string | null; itemName: string | null; condition: string | null; manufactureMonth: string | null; warrantyStatus: string | null; authenticatedEntryPoint: string }
export interface InstalledBaseRow { unitId: string; serial: string; itemCode: string; itemName: string | null; deliveryDate: string; customerPoRef: string | null; customerItemRef: string | null; isComponent: boolean; coverage: Coverage[] }
export interface InstalledBasePage { rows: InstalledBaseRow[]; hasMore: boolean; nextDeliveryDate: string | null; nextUnitId: string | null }
export interface ServiceHistoryRow { eventId: string; eventType: string; at: string; status: string | null }
export interface ServiceHistoryPage { rows: ServiceHistoryRow[]; hasMore: boolean; nextAt: string | null; nextSequence: number | null }
export interface CertificateRow { certificateId: string; kind: string; formType: string; version: number; trackingNumber: string; status: string; issuedAt: string; owner: { code: string; name: string | null } | null }
export interface CertificatePage { rows: CertificateRow[]; hasMore: boolean; nextIssuedAt: string | null; nextCertificateId: string | null }
export interface WhereUsedParent { unitId: string; serial: string; current: boolean; installedAt: string; removedAt: string | null; liveEntitlements: Coverage[] }
export interface WhereUsedResult { outcome: string; validationReasons: string[]; componentUnitId: string | null; parents: WhereUsedParent[]; componentEntitlements: Coverage[] }
export interface SerialListResult { matched: { input: string; unit: PortalUnit }[]; unmatched: string[]; invalid: { input: string; identifierType: string | null; reasons: string[] }[] }
export interface PortalRegistrationResult { registrationId: string; unitId: string; entitlementId: string; outcome: string; status: string; deliveryAt: string; evidenceAt: string; graceDays: number; startDate: string | null; replayed: boolean }
export interface PortalRmaResult { rmaId: string; rmaNumber: string; stage: string; replayed: boolean }

export interface ReconciliationRow { ordinal: number; input: string; identifierType: string | null; normalisedIdentifier: string | null; outcome: string; validationReasons: string[]; unit: string | null; item: string | null; otherOwner: string | null; assertionId: string | null; assertionStatus: string | null; customerPoRef: string | null; customerItemRef: string | null; unitAtCustomerSiteDefault: boolean | null }
export interface ReconciliationPage { reconciliationId: string; sourceKind: string; customer: string; total: number; owned: number; ownedByAnotherParty: number; notFound: number; invalid: number; items: ReconciliationRow[]; nextCursor: string | null; replayed: boolean }

export interface InternalLookup { serial: string; item: string; lifecycleStatus: string; disposition: string; identifiers: { type: string; value: string; current: boolean }[]; ownership: { owner: string; validFrom: string; validTo: string | null; current: boolean; customerPoReference: string | null; customerItemReference: string | null; saleChains: { order: string | null; delivery: string | null; invoice: string | null; deliveredAt: string | null; invoicedAt: string | null; warrantyLines: { line: number; item: string; price: number; currency: string; policyCode: string | null; internalNote: string | null }[]; internalNote: string | null }[] }[]; serviceHistory: { kind: string; reference: string; status: string; recordedAt: string; internalNote: string | null; cost: number | null; currency: string | null }[] }
export interface XrefRow { rootSerial: string; parentSerial: string | null; depth: number; slot: string | null; internalSerial: string; manufacturerSerial: string | null; vendorSerial: string | null; customerSerial: string | null; assetTag: string | null; item: string; currentOwner: string | null }
export interface XrefPage { items: XrefRow[]; nextCursor: string | null; selectedRoots: number }
export interface DisclosureRow { field: string; tier: string; enabled: boolean }
export interface DisclosurePolicy { publicTierEnabled: boolean; fields: DisclosureRow[]; settingsVersion: string }
export interface ReferenceOption { kind: string; id: string; type: string | null; code: string | null; name: string | null; number: string | null; status: string | null }
