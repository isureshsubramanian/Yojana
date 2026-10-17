import { test } from "node:test";
import assert from "node:assert/strict";
import {
  EMPTY_COMMAND_ID,
  buildCorrectAsBuiltArguments,
  buildCorrectDocumentSerialsArguments,
  buildCorrectSourceDocumentArguments,
  buildLateSerializationArguments,
} from "../src/corrections/workbenchForms.ts";

const context = { legalEntityId: "10000000-0000-0000-0000-000000000001", eventTime: "2026-10-17T08:30:00.000Z" };

test("CorrectAsBuilt arguments match the T17 command's nested target and link-change shape", () => {
  assert.deepEqual(buildCorrectAsBuiltArguments({
    parentUnitId: "20000000-0000-0000-0000-000000000001",
    effectiveAt: "2026-10-16T10:00:00.000Z",
    changes: [{
      change: "replace",
      removedLinkId: "30000000-0000-0000-0000-000000000001",
      slot: "component-1",
      relation: "component",
      component: { kind: "serial", serial: "CAB-CHILD-002", identifierType: "internal_serial" },
    }],
  }, context), {
    command_id: EMPTY_COMMAND_ID,
    targets: [{
      parent: { unit_id: "20000000-0000-0000-0000-000000000001" },
      effective_at: "2026-10-16T10:00:00.000Z",
      changes: [{
        change: "replace",
        removed_link_id: "30000000-0000-0000-0000-000000000001",
        installed: { unit: { serial: "CAB-CHILD-002", identifier_type: "internal_serial" }, relation: "component", slot: "component-1" },
      }],
    }],
    reason: "",
    actor_ref: "",
    legal_entity_id: context.legalEntityId,
  });
});

test("LateSerialization arguments match the T17 stock commissioning shape", () => {
  const source = { type: "stocktake", id: "40000000-0000-0000-0000-000000000001" };
  assert.deepEqual(buildLateSerializationArguments({
    itemId: "50000000-0000-0000-0000-000000000001",
    effectiveAt: "2026-10-16T09:00:00.000Z",
    sourceDocument: source,
    identifierType: "internal_serial",
    stock: [{
      commissionCommandId: "60000000-0000-0000-0000-000000000001",
      locationId: "70000000-0000-0000-0000-000000000001",
      locationKind: "warehouse",
      serials: ["LATE-001"],
      exemptQuantity: 0,
      exemptReason: null,
      origin: "purchased",
    }],
    kitCaptures: [],
    asBuiltCommandId: null,
  }, context), {
    command_id: EMPTY_COMMAND_ID,
    item_id: "50000000-0000-0000-0000-000000000001",
    effective_at: "2026-10-16T09:00:00.000Z",
    reason: "",
    actor_ref: "",
    legal_entity_id: context.legalEntityId,
    source_document: source,
    stock: [{
      commission_command_id: "60000000-0000-0000-0000-000000000001",
      location_id: "70000000-0000-0000-0000-000000000001",
      location_kind: "warehouse",
      source_document: source,
      units: [{ identifiers: [{ type: "internal_serial", raw_value: "LATE-001" }] }],
      exempt_quantity: 0,
      origin: "purchased",
    }],
    kit_captures: [],
  });
});

test("CorrectDocumentSerials arguments match the T17 document and two-selection shape", () => {
  assert.deepEqual(buildCorrectDocumentSerialsArguments({
    document: { type: "transfer", id: "80000000-0000-0000-0000-000000000001" },
    recordedSelectionId: "90000000-0000-0000-0000-000000000001",
    physicalSelectionId: "90000000-0000-0000-0000-000000000002",
  }, context), {
    command_id: EMPTY_COMMAND_ID,
    document: { type: "transfer", id: "80000000-0000-0000-0000-000000000001" },
    recorded_selection_id: "90000000-0000-0000-0000-000000000001",
    physical_selection_id: "90000000-0000-0000-0000-000000000002",
    reason: "",
    event_time: context.eventTime,
    actor_ref: "",
    legal_entity_id: context.legalEntityId,
  });
});

test("CorrectSourceDocument arguments match the T17 selection and before/after document shape", () => {
  assert.deepEqual(buildCorrectSourceDocumentArguments({
    selectionId: "a0000000-0000-0000-0000-000000000001",
    previousDocument: { type: "delivery", id: "b0000000-0000-0000-0000-000000000001" },
    correctedDocument: { type: "delivery", id: "b0000000-0000-0000-0000-000000000002" },
  }, context), {
    command_id: EMPTY_COMMAND_ID,
    selection_id: "a0000000-0000-0000-0000-000000000001",
    previous_document: { type: "delivery", id: "b0000000-0000-0000-0000-000000000001" },
    corrected_document: { type: "delivery", id: "b0000000-0000-0000-0000-000000000002" },
    reason: "",
    event_time: context.eventTime,
    actor_ref: "",
    legal_entity_id: context.legalEntityId,
  });
});
