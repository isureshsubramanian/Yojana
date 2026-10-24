import type { DocumentValue } from "./ReferenceFields";

export const EMPTY_COMMAND_ID = "00000000-0000-0000-0000-000000000000";

export interface WorkbenchFormContext {
  eventTime: string;
}

export interface AsBuiltFormChange {
  change: "add" | "remove" | "replace";
  removedLinkId?: string;
  slot: string;
  relation: string;
  component: { kind: "serial"; serial: string; identifierType: string } | { kind: "item"; itemId: string; quantity: number };
}

export interface CorrectAsBuiltFormValue {
  parentUnitId: string;
  effectiveAt: string;
  changes: AsBuiltFormChange[];
}

export interface LateSerializationStockValue {
  commissionCommandId: string;
  locationId: string | null;
  locationKind: string;
  serials: string[];
  exemptQuantity: number;
  exemptReason: string | null;
  origin: string;
}

export interface LateSerializationKitValue {
  commissionCommandId: string;
  existingLinkId: string;
  fittedAt: string;
  serials: string[];
  origin: string;
}

export interface LateSerializationFormValue {
  itemId: string;
  effectiveAt: string;
  sourceDocument: DocumentValue;
  identifierType: string;
  stock: LateSerializationStockValue[];
  kitCaptures: LateSerializationKitValue[];
  asBuiltCommandId: string | null;
}

export interface CorrectDocumentSerialsFormValue {
  document: DocumentValue;
  recordedSelectionId: string;
  physicalSelectionId: string;
}

export interface CorrectSourceDocumentFormValue {
  selectionId: string;
  previousDocument: DocumentValue;
  correctedDocument: DocumentValue;
}

export interface CorrectReplacementLinkFormValue {
  recordedReplacementUnitId: string;
  actualReplacementUnitId: string;
}

const commissionLines = (serials: string[], identifierType: string) => serials.map((serial) => ({
  identifiers: [{ type: identifierType, raw_value: serial }],
}));

/** Pure UI-to-command adapter. Trusted command, actor, reason and approval values are stamped by the dispatcher. */
export function buildCorrectAsBuiltArguments(value: CorrectAsBuiltFormValue, context: WorkbenchFormContext): Record<string, unknown> {
  return {
    command_id: EMPTY_COMMAND_ID,
    targets: [{
      parent: { unit_id: value.parentUnitId },
      effective_at: value.effectiveAt,
      changes: value.changes.map((row) => ({
        change: row.change,
        ...(row.removedLinkId ? { removed_link_id: row.removedLinkId } : {}),
        ...(row.change === "remove" ? {} : {
          installed: row.component.kind === "serial"
            ? { unit: { serial: row.component.serial, identifier_type: row.component.identifierType }, relation: row.relation, slot: row.slot || null }
            : { item_id: row.component.itemId, quantity: row.component.quantity, relation: row.relation, slot: row.slot || null },
        }),
      })),
    }],
    reason: "",
    actor_ref: "",
  };
}

/** Pure UI-to-command adapter matching the nested stock and kit-capture records accepted by LateSerialization. */
export function buildLateSerializationArguments(value: LateSerializationFormValue, context: WorkbenchFormContext): Record<string, unknown> {
  return {
    command_id: EMPTY_COMMAND_ID,
    item_id: value.itemId,
    effective_at: value.effectiveAt,
    reason: "",
    actor_ref: "",
    source_document: value.sourceDocument,
    stock: value.stock.map((row) => ({
      commission_command_id: row.commissionCommandId,
      location_id: row.locationId,
      location_kind: row.locationKind,
      source_document: value.sourceDocument,
      units: commissionLines(row.serials, value.identifierType),
      exempt_quantity: row.exemptQuantity,
      ...(row.exemptReason ? { exempt_reason: row.exemptReason } : {}),
      origin: row.origin,
    })),
    kit_captures: value.kitCaptures.map((row) => ({
      commission_command_id: row.commissionCommandId,
      existing_link_id: row.existingLinkId,
      fitted_at: row.fittedAt,
      source_document: value.sourceDocument,
      units: commissionLines(row.serials, value.identifierType),
      origin: row.origin,
    })),
    ...(value.asBuiltCommandId ? { as_built_command_id: value.asBuiltCommandId } : {}),
  };
}

/** The two user-entered serial lists are resolved separately; the command receives their immutable selection ids. */
export function buildCorrectDocumentSerialsArguments(value: CorrectDocumentSerialsFormValue, context: WorkbenchFormContext): Record<string, unknown> {
  return {
    command_id: EMPTY_COMMAND_ID,
    document: value.document,
    recorded_selection_id: value.recordedSelectionId,
    physical_selection_id: value.physicalSelectionId,
    reason: "",
    event_time: context.eventTime,
    actor_ref: "",
  };
}

/** The affected serials are frozen in one selection before the source-document correction is previewed. */
export function buildCorrectSourceDocumentArguments(value: CorrectSourceDocumentFormValue, context: WorkbenchFormContext): Record<string, unknown> {
  return {
    command_id: EMPTY_COMMAND_ID,
    selection_id: value.selectionId,
    previous_document: value.previousDocument,
    corrected_document: value.correctedDocument,
    reason: "",
    event_time: context.eventTime,
    actor_ref: "",
  };
}

/** Both sides are unit references; policy, reason and customer facts are recovered from the recorded replacement. */
export function buildCorrectReplacementLinkArguments(value: CorrectReplacementLinkFormValue, context: WorkbenchFormContext): Record<string, unknown> {
  return {
    command_id: EMPTY_COMMAND_ID,
    recorded_replacement_unit_id: value.recordedReplacementUnitId,
    actual_replacement_unit_id: value.actualReplacementUnitId,
    reason: "",
    event_time: context.eventTime,
    actor_ref: "",
  };
}
