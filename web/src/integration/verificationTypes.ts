import * as z from "zod/mini";

export const acceptanceItemResult = z.enum(["pending", "passed", "failed", "not_applicable"]);
export type AcceptanceItemResult = z.infer<typeof acceptanceItemResult>;
export const verificationFactKind = z.enum(["documented", "inferred", "assumed"]);
export type VerificationFactKind = z.infer<typeof verificationFactKind>;
export const connectorLiveVerification = z.object({ verified: z.boolean(), release: z.nullable(z.string()),
  date: z.nullable(z.string()), scenarioCount: z.nullable(z.number()) });
export type ConnectorLiveVerification = z.infer<typeof connectorLiveVerification>;
export const verificationFact = z.object({ id: z.string(), fact: z.string(), valueUsed: z.string(),
  kind: verificationFactKind, sourceLabel: z.string(), dateRead: z.string(), usedAt: z.string(),
  acceptanceItem: z.boolean() });
export type VerificationFact = z.infer<typeof verificationFact>;
export const acceptanceRunSummary = z.object({ runId: z.string(), sandboxLabel: z.string(), status: z.string(),
  startedBy: z.string(), startedAt: z.string(), completedBy: z.nullable(z.string()),
  completedAt: z.nullable(z.string()), passed: z.number(), failed: z.number(), notApplicable: z.number(),
  pending: z.number() });
export type AcceptanceRunSummary = z.infer<typeof acceptanceRunSummary>;
export const connectorVerificationView = z.object({ connectionId: z.string(), connectionName: z.string(),
  connectorCode: z.string(), liveVerification: connectorLiveVerification, acceptancePassed: z.boolean(),
  documentedFacts: z.number(), inferredFacts: z.number(), assumedFacts: z.number(), edgeScenarios: z.number(),
  edgeScenariosApplicable: z.number(), facts: z.array(verificationFact), runs: z.array(acceptanceRunSummary),
  features: z.nullable(z.object({ tenantSideExtension: z.boolean(), interactiveSignIn: z.boolean() })) });
export type ConnectorVerificationView = z.infer<typeof connectorVerificationView>;
export const acceptanceItem = z.object({ itemId: z.string(), code: z.string(), kind: z.string(), label: z.string(),
  result: acceptanceItemResult, note: z.nullable(z.string()), evidence: z.nullable(z.string()),
  markedBy: z.nullable(z.string()), markedAt: z.nullable(z.string()) });
export type AcceptanceItem = z.infer<typeof acceptanceItem>;
export const acceptanceRun = z.object({ runId: z.string(), connectionId: z.string(), connectionName: z.string(),
  connectorCode: z.string(), sandboxLabel: z.string(), status: z.string(), startedBy: z.string(),
  startedAt: z.string(), completedBy: z.nullable(z.string()), completedAt: z.nullable(z.string()),
  items: z.array(acceptanceItem) });
export type AcceptanceRun = z.infer<typeof acceptanceRun>;
export const edgeImportResult = z.object({ runId: z.string(), imported: z.number(), contentHash: z.string() });
export type EdgeImportResult = z.infer<typeof edgeImportResult>;

