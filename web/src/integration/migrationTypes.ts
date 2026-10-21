import type { CursorPage } from "./adminTypes";

export interface MigrationSource {
  sourceId: string;
  displayLabel: string;
  sourceSystem: string;
  runtime: string;
  route: string;
  databaseName: string;
  schemaName: string;
  linkedServerName: string | null;
  commandTimeoutSeconds: number;
  hasConnectionString: boolean;
  licencePosition: string;
  licenceConfirmedByName: string | null;
  licenceConfirmedAt: string | null;
  licenceContractReference: string | null;
  licenceNote: string | null;
  createdAt: string;
  updatedAt: string;
}

export type MigrationSourcePage = CursorPage<MigrationSource>;

// table is the source's own name, shown only as data under the readable label the server supplies.
export interface MigrationSourceTestTable { table: string; rowCount: number | null; error: string | null; role: string; label: string | null }
export interface MigrationSourceTestResult { sourceId: string; tables: MigrationSourceTestTable[] }

export interface MigrationBatchTable {
  table: string;
  role: string;
  label: string;
  rowsRead: number;
  inserted: number;
  updated: number;
  unchanged: number;
  absent: number;
  durationMilliseconds: number;
}

export interface MigrationBatch {
  batchId: string;
  sourceId: string;
  sourceSystem: string;
  kind: string;
  requestedByName: string;
  requestedAt: string;
  startedAt: string | null;
  finishedAt: string | null;
  status: string;
  failure: string | null;
  tables: MigrationBatchTable[] | null;
}

export type MigrationBatchPage = CursorPage<MigrationBatch>;
export interface StartMigrationBatchResponse { batchId: string; status: string; failure: string | null }

export interface MigrationProfileClass {
  code: string;
  name: string;
  outcome: MigrationOutcome;
  outcomeLabel: string;
  count: number | null;
  change: number | null;
  note: string | null;
}

export interface MigrationProfile {
  runId: string;
  batchId: string;
  startedAt: string | null;
  finishedAt: string | null;
  status: string;
  classes: MigrationProfileClass[];
}

export interface MigrationProfileSample {
  sourceKey: string;
  serial: string | null;
  item: string | null;
  facts: Record<string, string | null>;
}

export interface MigrationProfileSamples { runId: string; classCode: string; samples: MigrationProfileSample[] }

export type MigrationOutcome = "stop_load" | "needs_person" | "fixed_by_rule" | "loaded_with_note";
export interface MigrationOutcomeChoice { classCode: string; outcome: MigrationOutcome }
export interface MigrationOutcomeSettings { classes: MigrationOutcomeChoice[]; version: string }

export type MigrationReviewKind = "same_serial_one_item" | "same_serial_different_items" | "orphan_equipment_card" | "serial_in_user_field";
export type MigrationReviewStatus = "waiting" | "decided" | "carried_over";
export type MigrationReviewDecision = "merge_into" | "keep_apart" | "fix_in_incumbent";

export interface CanonicalisationGap { kind: MigrationReviewKind; reason: string }
export interface CanonicalisationRun {
  runId: string;
  batchId: string;
  startedAt: string | null;
  finishedAt: string | null;
  status: string;
  failure: string | null;
  serialsInSource: number;
  exact: number;
  trimmedUpper: number;
  familyNormalised: number;
  waitingForPerson: number;
  merged: number;
  keptApart: number;
  fixInIncumbent: number;
  carriedOver: number;
  unverifiedDates: number;
  gaps: CanonicalisationGap[];
}

export interface MigrationReviewRow {
  itemId: string;
  serialAsStored: string | null;
  itemCode: string | null;
  itemName: string | null;
  family: string | null;
  kind: MigrationReviewKind;
  ruleTried: string;
  candidateSummary: string;
  suggestion: MigrationReviewDecision | null;
  suggestionReason: string | null;
  hint: string | null;
  status: MigrationReviewStatus;
  carriedOver: boolean;
}

export interface MigrationReviewPage { items: MigrationReviewRow[]; nextCursor: string | null }

export interface MigrationCandidateLineage {
  sourceKey: string;
  serialAsStored: string | null;
  itemCode: string;
  itemName: string | null;
  transactions: number;
  firstReceipt: string | null;
  lastDelivery: string | null;
  lastDeliveryParty: string | null;
  hasEquipmentCard: boolean;
  hasContract: boolean;
  disposition: string;
}

export interface MigrationReviewDecisionView {
  decisionId: string;
  decision: MigrationReviewDecision;
  keepSourceKey: string | null;
  reason: string;
  note: string | null;
  decidedByName: string;
  decidedAt: string;
  appliedToThisBatch: boolean;
}

export interface MigrationReviewDetail {
  itemId: string;
  kind: MigrationReviewKind;
  status: MigrationReviewStatus;
  serialAsStored: string | null;
  itemCode: string | null;
  itemName: string | null;
  ruleTried: string;
  candidates: MigrationCandidateLineage[];
  suggestion: MigrationReviewDecision | null;
  suggestedKeepSourceKey: string | null;
  suggestionReason: string | null;
  hint: string | null;
  earlierDecisions: MigrationReviewDecisionView[];
}

export interface MigrationReviewDecisionResponse {
  decisionId: string;
  itemId: string;
  decision: MigrationReviewDecision;
  status: MigrationReviewStatus;
  decidedAt: string;
}
