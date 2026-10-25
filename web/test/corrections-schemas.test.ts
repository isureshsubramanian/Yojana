import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import * as z from "zod/mini";
import { checkedBody } from "../src/api/client.ts";
import {
  correctionKpiReport,
  reassignAllocationRequest,
  reassignAllocationResult,
} from "../src/corrections/types.ts";
import {
  itemFamilyOptions,
  referenceNamesResponse,
  referenceSearchResponse,
} from "../src/corrections/referenceSchemas.ts";
import { workbenchUnitLineage } from "../src/corrections/workbenchApi.ts";

const correctionPreview = {
  selectionId: null,
  command: "assign_identifiers",
  unitCount: 1,
  change: 1,
  skip: 0,
  block: 0,
  units: [{ unitId: "10000000-0000-0000-0000-000000000001", serial: "TMP-001", outcome: "change", reason: null, detail: null, unblockingStep: null }],
  unitEvents: [{ unitId: "10000000-0000-0000-0000-000000000001", eventType: "unit.identifier_assigned", effectiveAt: "2026-10-25T09:30:00Z", summary: null }],
  entitlementChanges: [],
  certificateVersions: [],
  printJobs: [],
  refusal: null,
  allocationReassignments: null,
  unitsToCommission: null,
  exemptions: null,
  kitsNeedingAsBuiltCapture: null,
};

const kpiResponse = {
  from: "2026-01-01",
  to: "2026-10-25",
  breakdown: "root_cause",
  itemId: null,
  baseline: { status: "ready", historyDays: 298, daysRemaining: 0, startedOn: "2026-01-01", targetsEffectiveOn: null },
  rows: [{ bucket: "2026-10", dimension: "1", corrections: 2, unitsCorrected: 2, unitsShipped: 400, perThousand: 5, reversedLater: 0, targetPerThousand: null }],
  unclassifiedAlert: { thresholdPercent: 10, ratePercent: 5, unclassifiedRequests: 1, corrections: 20, alert: false, months: null },
};

const identifier = {
  identifierId: "20000000-0000-0000-0000-000000000001",
  type: "internal_serial",
  scheme: "internal",
  rawValue: "SER-001",
  normalisedValue: "SER-001",
  scopeKey: "global",
  issuerPartyRef: null,
  qualifier: null,
  validFrom: "2026-10-20T08:00:00Z",
  validTo: null,
  supersededByIdentifierId: null,
  supersedesIdentifierId: null,
  correctionReason: null,
  actorRef: "operator@example.test",
};

const unitState = {
  unitId: "10000000-0000-0000-0000-000000000001",
  lifecycleStatus: "commissioned",
  origin: "manufactured",
  serialisationPoint: "production",
  retentionClass: "full",
  historyDepth: "complete",
  lotId: null,
  expiryDate: null,
  derivedFromUnitId: null,
  mergedIntoUnitId: null,
  itemId: "30000000-0000-0000-0000-000000000001",
  modelVersionId: null,
  disposition: "active",
  conditionGrade: null,
  locationId: null,
  locationKind: null,
  custodyPartyRef: null,
  ownerPartyRef: null,
  openOwnershipEpisodeId: null,
  openCustodyEpisodeId: null,
  overlayFlags: "none",
  lastEventSeq: 4,
  lastEventTime: "2026-10-25T09:30:00Z",
  identifiers: [identifier],
  episodes: [],
  asBuilt: [],
  buildRecords: [],
  parentUnitId: null,
  eventCount: 4,
  ownerAndHolderDiffer: false,
};

const lineageResponse = {
  unitId: unitState.unitId,
  state: unitState,
  identifiers: [identifier],
  itemAssignments: [{ assignmentId: "40000000-0000-0000-0000-000000000001", itemId: unitState.itemId, modelVersionId: null, validFrom: "2026-10-20T08:00:00Z", validTo: null, reason: "commissioned" }],
  episodes: [],
  entitlements: [],
  assembly: [],
  merges: [],
  corrections: [{ correctionId: "50000000-0000-0000-0000-000000000001", command: "assign_identifiers", reasonCode: "vendor_serial_received", initiatedVia: "workbench", initiatingRole: "operations", actorRef: "operator@example.test", eventTime: "2026-10-25T09:30:00Z", recordedTime: "2026-10-25T09:30:01Z", correctsCorrectionId: null, note: null }],
  lastEvents: [{ eventId: "60000000-0000-0000-0000-000000000001", seq: 4, eventType: "unit.identifier_assigned", eventTime: "2026-10-25T09:30:00Z", recordedTime: "2026-10-25T09:30:01Z", bizStep: null, disposition: "active", locationId: null, actorRef: "operator@example.test", document: { type: "production_order", id: "70000000-0000-0000-0000-000000000001", line: null }, attributes: null }],
};

test("correction response schemas accept C# DTO nullability, strip additions, and refuse missing required fields", () => {
  const parsed = z.parse(correctionKpiReport, { ...kpiResponse, futureServerField: "ignored" });
  assert.deepEqual(parsed, kpiResponse);
  assert.equal(z.safeParse(reassignAllocationResult, {
    correctionId: "80000000-0000-0000-0000-000000000001", eventId: null, preview: correctionPreview, replayed: false,
  }).success, true);
  const { baseline: _baseline, ...withoutBaseline } = kpiResponse;
  assert.equal(z.safeParse(correctionKpiReport, withoutBaseline).success, false);
  assert.equal(correctionPreview.unitsToCommission, null);
});

test("correction request schemas validate declared fields and retain extensions", () => {
  const request = {
    commandId: "80000000-0000-0000-0000-000000000001",
    fromProductionOrder: { type: "production_order", id: "70000000-0000-0000-0000-000000000001", sourceRevision: "v2" },
    toProductionOrder: { type: "production_order", id: "70000000-0000-0000-0000-000000000002" },
    mode: "move",
    fromSerials: { kind: "pasted_list", serials: ["SER-001"], sourceHint: "scanner" },
    toSerials: null,
    reason: "wrong_allocation",
    eventTime: "2026-10-25T09:30:00Z",
    actorRef: "",
    note: null,
    requestExtension: true,
  };
  assert.equal(checkedBody(reassignAllocationRequest, request), request);
  assert.throws(() => checkedBody(reassignAllocationRequest, { ...request, reason: 7 } as unknown as typeof request));
});

test("workbench lineage schema mirrors the complete UnitDetail and lineage records", () => {
  const parsed = z.parse(workbenchUnitLineage, { ...lineageResponse, futureServerField: "ignored" });
  assert.deepEqual(parsed, lineageResponse);
  const { merges: _merges, ...withoutMerges } = lineageResponse;
  assert.equal(z.safeParse(workbenchUnitLineage, withoutMerges).success, false);
});

test("reference response schemas accept realistic labels and searches and refuse omissions", () => {
  const itemId = unitState.itemId;
  const references = {
    items: { [itemId]: { code: "FG-100", name: "Field gateway" } },
    parties: { "90000000-0000-0000-0000-000000000001": { code: "C-200", name: null, partyType: "customer" } },
    documents: { "production_order:70000000-0000-0000-0000-000000000001": { number: "MO-2026-731", status: "released", itemId } },
    locations: { "a0000000-0000-0000-0000-000000000001": { code: "Q-01", name: "Quarantine bay", kind: "quarantine" } },
  };
  assert.equal(z.safeParse(referenceNamesResponse, references).success, true);
  const { locations: _locations, ...withoutLocations } = references;
  assert.equal(z.safeParse(referenceNamesResponse, withoutLocations).success, false);
  assert.equal(z.safeParse(itemFamilyOptions, [{ code: "gateway", name: "Gateway" }]).success, true);
  assert.equal(z.safeParse(referenceSearchResponse, { results: [{ kind: "unit", id: unitState.unitId, type: null, code: null, name: null, partyType: null, number: "SER-001", status: "commissioned", itemId }] }).success, true);
});

test("every assigned correction API call is schema checked", () => {
  const files = ["corrections/api.ts", "corrections/workbenchApi.ts", "corrections/ReferenceFields.tsx"];
  const sources = files.map((path) => readFileSync(new URL(`../src/${path}`, import.meta.url), "utf8"));
  for (const [index, source] of sources.entries()) {
    assert.doesNotMatch(source, /\bapi(?:<|\()/, `${files[index]} has no unchecked api call`);
  }
  assert.equal(sources.reduce((total, source) => total + [...source.matchAll(/\bapiParsed\(/g)].length, 0), 28);
});
