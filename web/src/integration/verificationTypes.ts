export type VerificationFactKind = "documented" | "inferred" | "assumed";
export type AcceptanceItemResult = "pending" | "passed" | "failed" | "not_applicable";

export interface ConnectorLiveVerification {
  verified: boolean;
  release: string | null;
  date: string | null;
  scenarioCount: number | null;
}

export interface VerificationFact {
  id: string;
  fact: string;
  valueUsed: string;
  kind: VerificationFactKind;
  sourceLabel: string;
  dateRead: string;
  usedAt: string;
  acceptanceItem: boolean;
}

export interface AcceptanceRunSummary {
  runId: string;
  sandboxLabel: string;
  status: string;
  startedBy: string;
  startedAt: string;
  completedBy: string | null;
  completedAt: string | null;
  passed: number;
  failed: number;
  notApplicable: number;
  pending: number;
}

export interface ConnectorVerificationView {
  connectionId: string;
  connectionName: string;
  connectorCode: string;
  liveVerification: ConnectorLiveVerification;
  acceptancePassed: boolean;
  documentedFacts: number;
  inferredFacts: number;
  assumedFacts: number;
  edgeScenarios: number;
  edgeScenariosApplicable: number;
  facts: VerificationFact[];
  runs: AcceptanceRunSummary[];
  features: { tenantSideExtension: boolean; interactiveSignIn: boolean };
}

export interface AcceptanceItem {
  itemId: string;
  code: string;
  kind: string;
  label: string;
  result: AcceptanceItemResult;
  note: string | null;
  evidence: string | null;
  markedBy: string | null;
  markedAt: string | null;
}

export interface AcceptanceRun {
  runId: string;
  connectionId: string;
  connectionName: string;
  connectorCode: string;
  sandboxLabel: string;
  status: string;
  startedBy: string;
  startedAt: string;
  completedBy: string | null;
  completedAt: string | null;
  items: AcceptanceItem[];
}

export interface EdgeImportResult { runId: string; imported: number; contentHash: string }
