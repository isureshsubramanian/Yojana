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
