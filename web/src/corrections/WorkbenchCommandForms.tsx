import { useEffect, useMemo, useRef, useState, type ClipboardEvent, type KeyboardEvent } from "react";
import { newCommandId, selections } from "../api/units";
import { ErrorNote, useActor } from "../units/common";
import { humanise, shortId, splitSerials } from "../units/format";
import { DocumentField, ItemField, UnitField, useReferenceNames, type DocumentValue } from "./ReferenceFields";
import type { WorkbenchCandidate, WorkbenchUnitLineage } from "./workbenchApi";
import {
  buildCorrectAsBuiltArguments,
  buildCorrectDocumentSerialsArguments,
  buildCorrectSourceDocumentArguments,
  buildCorrectReplacementLinkArguments,
  buildLateSerializationArguments,
  type AsBuiltFormChange,
  type LateSerializationKitValue,
  type LateSerializationStockValue,
} from "./workbenchForms";

interface CommandFormProps {
  command: string;
  unitId: string | null;
  primary: string | null;
  candidates: WorkbenchCandidate[];
  lineage: WorkbenchUnitLineage | null;
  initialDocument: DocumentValue | null;
  initialSelectionId: string | null;
  reason: string;
  corrects: string;
  onArguments: (value: Record<string, unknown> | null) => void;
}

export const STRUCTURED_COMMANDS = ["correct_as_built", "late_serialization", "correct_document_serials", "correct_source_document", "correct_replacement_link"] as const;

export function StructuredCommandForm(props: CommandFormProps) {
  const actor = useActor();
  switch (props.command) {
    case "correct_as_built": return <CorrectAsBuiltForm {...props} />;
    case "late_serialization": return <LateSerializationForm {...props} />;
    case "correct_document_serials": return <CorrectDocumentSerialsForm {...props} actor={actor} />;
    case "correct_source_document": return <CorrectSourceDocumentForm {...props} actor={actor} />;
    case "correct_replacement_link": return <CorrectReplacementLinkForm {...props} />;
    default: return null;
  }
}

function CorrectReplacementLinkForm({ unitId, primary, onArguments }: CommandFormProps) {
  const [actualUnitId, setActualUnitId] = useState<string | null>(null);
  const invalid = !unitId || !actualUnitId || actualUnitId === unitId;

  useEffect(() => {
    onArguments(invalid ? null : buildCorrectReplacementLinkArguments({
      recordedReplacementUnitId: unitId!,
      actualReplacementUnitId: actualUnitId!,
    }, context()));
  }, [unitId, actualUnitId, invalid]); // eslint-disable-line react-hooks/exhaustive-deps

  return <div className="workbench-command-form">
    <div className="correction-form-head"><div><strong>Correct a replacement link</strong><span>Compare the unit recorded on the shipment with the physical unit the customer received.</span></div></div>
    <div className="pair" style={{ marginBottom: 12 }}>
      <div className="side"><div className="k">Recorded replacement</div><div className="serial">{primary ?? "Selected unit"}</div><div className="hint">Returns to its prior company stock location.</div></div>
      <div className="arrow">→</div>
      <div className="side"><UnitField id="wb-actual-replacement" label="Actual replacement" value={actualUnitId} onChange={setActualUnitId} hint="Search or scan the unit physically supplied" /></div>
    </div>
    {actualUnitId === unitId && <Validation errors={["The recorded and actual replacement units must be different."]} />}
    <div className="hint">The command computes ownership, custody, entitlement transfer, asset-tag printing, and certificate reissue requests from the original replacement.</div>
  </div>;
}

type AsBuiltRow = {
  key: string;
  removedLinkId: string | null;
  currentUnitId: string | null;
  currentItemId: string | null;
  currentSerial: string | null;
  currentQuantity: number | null;
  change: "keep" | "add" | "remove" | "replace";
  slot: string;
  relation: string;
  kind: "serial" | "item";
  serial: string;
  identifierType: string;
  itemId: string | null;
  quantity: string;
};

function CorrectAsBuiltForm({ unitId, lineage, onArguments }: CommandFormProps) {
  const links = useMemo(() => lineage?.assembly.filter((row) => row.perspective === "child" && !row.removedAt) ?? [], [lineage]);
  const names = useReferenceNames({ items: links.map((row) => row.childItemId) });
  const [effectiveAt, setEffectiveAt] = useState(() => datetimeInput());
  const [rows, setRows] = useState<AsBuiltRow[]>([]);
  const initialised = useRef<string | null>(null);

  useEffect(() => {
    if (!unitId || initialised.current === unitId || !lineage) return;
    initialised.current = unitId;
    setRows(links.map((link) => {
      const detail = lineage.state.asBuilt.find((row) => row.linkId === link.linkId);
      return {
        key: link.linkId,
        removedLinkId: link.linkId,
        currentUnitId: link.childUnitId,
        currentItemId: link.childItemId,
        currentSerial: detail?.childSerial ?? null,
        currentQuantity: detail?.quantity ?? null,
        change: "keep" as const,
        slot: link.slot ?? "",
        relation: link.relation,
        kind: link.childUnitId ? "serial" as const : "item" as const,
        serial: "",
        identifierType: "internal_serial",
        itemId: link.childItemId,
        quantity: detail?.quantity ? String(detail.quantity) : "1",
      };
    }));
  }, [unitId, lineage, links]);

  const changed = rows.filter((row) => row.change !== "keep");
  const errors = !unitId ? ["Select a unit first."] : [
    ...(!effectiveAt ? ["Enter when the physical component change took effect."] : []),
    ...(changed.length === 0 ? ["Choose add, remove or replace for at least one component."] : []),
    ...changed.flatMap((row) => {
      if (row.change === "remove") return [];
      if (row.kind === "serial" && !row.serial.trim()) return [`${row.slot || "New component"} needs a component serial.`];
      if (row.kind === "item" && (!row.itemId || !(Number(row.quantity) > 0))) return [`${row.slot || "New component"} needs an item and a positive quantity.`];
      return [];
    }),
  ];

  useEffect(() => {
    if (errors.length || !unitId) { onArguments(null); return; }
    const changes: AsBuiltFormChange[] = changed.map((row) => ({
      change: row.change as AsBuiltFormChange["change"],
      ...(row.removedLinkId ? { removedLinkId: row.removedLinkId } : {}),
      slot: row.slot.trim(),
      relation: row.relation,
      component: row.kind === "serial"
        ? { kind: "serial", serial: row.serial.trim(), identifierType: row.identifierType }
        : { kind: "item", itemId: row.itemId!, quantity: Number(row.quantity) },
    }));
    onArguments(buildCorrectAsBuiltArguments({ parentUnitId: unitId, effectiveAt: toIso(effectiveAt), changes }, context()));
  }, [unitId, effectiveAt, rows]); // eslint-disable-line react-hooks/exhaustive-deps

  const update = (key: string, patch: Partial<AsBuiltRow>) => setRows((current) => current.map((row) => row.key === key ? { ...row, ...patch } : row));
  const add = () => setRows((current) => [...current, blankAsBuiltRow()]);
  const pasteSerials = (event: ClipboardEvent<HTMLInputElement>, row: AsBuiltRow) => {
    const incoming = splitSerials(event.clipboardData.getData("text"));
    if (incoming.length <= 1) return;
    event.preventDefault();
    setRows((current) => current.flatMap((candidate) => candidate.key !== row.key ? [candidate] : [
      { ...candidate, serial: incoming[0]! },
      ...incoming.slice(1).map((serial) => ({ ...blankAsBuiltRow(), slot: candidate.slot, relation: candidate.relation, serial })),
    ]));
  };

  return <div className="workbench-command-form">
    <div className="correction-form-head"><div><strong>Correct as-built parts</strong><span>Correct the effective-dated component tree; unchanged links stay untouched.</span></div><button type="button" className="btn sm" onClick={add}>Add component</button></div>
    <div className="field"><label htmlFor="wb-asbuilt-at">Physical change took effect</label><input id="wb-asbuilt-at" className="input" type="datetime-local" value={effectiveAt} max={datetimeInput()} onChange={(event) => setEffectiveAt(event.target.value)} /></div>
    <div className="correction-lines" data-serial-form="asbuilt">
      {rows.map((row, index) => <div className={`correction-line${row.change === "keep" ? " quiet" : ""}`} key={row.key}>
        <div className="correction-line-title"><span className="slot">{row.slot || `New component ${index + 1}`}</span><span className={row.currentSerial ? "serial" : "muted"}>{row.currentSerial ?? (row.currentUnitId ? `unit ${shortId(row.currentUnitId)}` : row.currentItemId ? `${names.item(row.currentItemId)}${row.currentQuantity ? ` · ${row.currentQuantity}` : ""}` : "not yet recorded")}</span></div>
        <div className="correction-line-controls">
          <div className="field"><label htmlFor={`wb-asbuilt-change-${row.key}`}>Action</label><select id={`wb-asbuilt-change-${row.key}`} className="select" value={row.change} onChange={(event) => update(row.key, { change: event.target.value as AsBuiltRow["change"] })}>
            {row.removedLinkId && <option value="keep">Keep</option>}<option value={row.removedLinkId ? "replace" : "add"}>{row.removedLinkId ? "Replace" : "Add"}</option>{row.removedLinkId && <option value="remove">Remove</option>}
          </select></div>
          <div className="field"><label htmlFor={`wb-asbuilt-slot-${row.key}`}>Slot</label><input id={`wb-asbuilt-slot-${row.key}`} className="input" value={row.slot} onChange={(event) => update(row.key, { slot: event.target.value })} /></div>
          {row.change !== "keep" && row.change !== "remove" && <>
            <div className="field"><label htmlFor={`wb-asbuilt-kind-${row.key}`}>Component kind</label><select id={`wb-asbuilt-kind-${row.key}`} className="select" value={row.kind} onChange={(event) => update(row.key, { kind: event.target.value as AsBuiltRow["kind"] })}><option value="serial">Serialised unit</option><option value="item">Quantity component</option></select></div>
            <div className="field"><label htmlFor={`wb-asbuilt-relation-${row.key}`}>Relationship</label><select id={`wb-asbuilt-relation-${row.key}`} className="select" value={row.relation} onChange={(event) => update(row.key, { relation: event.target.value })}><option value="component">Component</option><option value="kit">Kit</option><option value="co_product_sibling">Co-product sibling</option></select></div>
            {row.kind === "serial" ? <div className="field"><label htmlFor={`wb-asbuilt-serial-${row.key}`}>Component serial</label><input id={`wb-asbuilt-serial-${row.key}`} data-serial-row className="input serial" value={row.serial} onChange={(event) => update(row.key, { serial: event.target.value })} onPaste={(event) => pasteSerials(event, row)} onKeyDown={(event) => focusNextSerial(event, "asbuilt", add)} /></div> : <>
              <ItemField id={`wb-asbuilt-item-${row.key}`} label="Component item" value={row.itemId} onChange={(value) => update(row.key, { itemId: value })} />
              <div className="field"><label htmlFor={`wb-asbuilt-quantity-${row.key}`}>Quantity</label><input id={`wb-asbuilt-quantity-${row.key}`} className="input" type="number" min="0.0001" step="any" value={row.quantity} onChange={(event) => update(row.key, { quantity: event.target.value })} /></div>
            </>}
          </>}
          {!row.removedLinkId && <button type="button" className="btn ghost sm correction-line-remove" onClick={() => setRows((current) => current.filter((candidate) => candidate.key !== row.key))}>Remove row</button>}
        </div>
      </div>)}
      {rows.length === 0 && <div className="correction-empty-row">No captured components. Add the component that is physically fitted.</div>}
    </div>
    <Validation errors={errors} />
  </div>;
}

function blankAsBuiltRow(): AsBuiltRow {
  return { key: crypto.randomUUID(), removedLinkId: null, currentUnitId: null, currentItemId: null, currentSerial: null, currentQuantity: null, change: "add", slot: "", relation: "component", kind: "serial", serial: "", identifierType: "internal_serial", itemId: null, quantity: "1" };
}

type StockUi = LateSerializationStockValue & { key: string; serials: string[]; exemptText: string };
type KitUi = LateSerializationKitValue & { key: string; serials: string[] };

function LateSerializationForm({ lineage, initialDocument, corrects, onArguments }: CommandFormProps) {
  const [itemId, setItemId] = useState<string | null>(lineage?.state.itemId ?? null);
  const [effectiveAt, setEffectiveAt] = useState(() => datetimeInput());
  const [sourceDocument, setSourceDocument] = useState<DocumentValue | null>(initialDocument);
  const [identifierType, setIdentifierType] = useState("internal_serial");
  const [stock, setStock] = useState<StockUi[]>([]);
  const [kits, setKits] = useState<KitUi[]>([]);
  const itemLinks = lineage?.assembly.filter((row) => row.perspective === "child" && !row.removedAt && row.childItemId === itemId) ?? [];
  const names = useReferenceNames({ items: [lineage?.state.itemId, ...itemLinks.map((row) => row.childItemId)], documents: [sourceDocument] });

  useEffect(() => { if (!itemId && lineage?.state.itemId) setItemId(lineage.state.itemId); }, [lineage?.state.itemId]); // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => { if (!sourceDocument && initialDocument) setSourceDocument(initialDocument); }, [initialDocument]); // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => {
    if (stock.length || !lineage) return;
    setStock([blankStock(lineage.state.locationId, lineage.state.locationKind ?? "warehouse")]);
  }, [lineage]); // eslint-disable-line react-hooks/exhaustive-deps

  const errors = [
    ...(!itemId ? ["Choose the item whose tracking mode is changing."] : []),
    ...(!effectiveAt ? ["Enter when serial tracking took effect."] : []),
    ...(!sourceDocument ? ["Choose the source document for the tracking-mode change."] : []),
    ...(!corrects && stock.length === 0 && kits.length === 0 ? ["Add stock serials, an installed kit capture, or an explicit stock exemption."] : []),
    ...stock.flatMap((row, index) => !row.locationKind ? [`Stock location ${index + 1} needs a location kind.`] : row.serials.every((serial) => !serial.trim()) && !(Number(row.exemptText) > 0) ? [`Stock location ${index + 1} needs serials or an exempt quantity.`] : Number(row.exemptText) > 0 && !row.exemptReason?.trim() ? [`Stock location ${index + 1} needs an exemption reason.`] : []),
    ...kits.flatMap((row, index) => !row.existingLinkId ? [`Installed capture ${index + 1} needs an existing component line.`] : row.serials.every((serial) => !serial.trim()) ? [`Installed capture ${index + 1} needs at least one serial.`] : []),
  ];

  useEffect(() => {
    if (errors.length || !itemId || !sourceDocument) { onArguments(null); return; }
    onArguments(buildLateSerializationArguments({
      itemId,
      effectiveAt: toIso(effectiveAt),
      sourceDocument,
      identifierType,
      stock: stock.map(({ key: _, exemptText, ...row }) => ({ ...row, serials: cleanSerials(row.serials), exemptQuantity: Number(exemptText) || 0, exemptReason: row.exemptReason?.trim() || null })),
      kitCaptures: kits.map(({ key: _, ...row }) => ({ ...row, fittedAt: toIso(row.fittedAt), serials: cleanSerials(row.serials) })),
      asBuiltCommandId: kits.length ? newCommandId() : null,
    }, context()));
  }, [itemId, effectiveAt, sourceDocument, identifierType, stock, kits, corrects]); // eslint-disable-line react-hooks/exhaustive-deps

  const updateStock = (key: string, patch: Partial<StockUi>) => setStock((rows) => rows.map((row) => row.key === key ? { ...row, ...patch } : row));
  const updateKit = (key: string, patch: Partial<KitUi>) => setKits((rows) => rows.map((row) => row.key === key ? { ...row, ...patch } : row));

  return <div className="workbench-command-form">
    <div className="correction-form-head"><div><strong>Serialise an item late</strong><span>Turn on serial tracking, commission existing stock, and capture units already fitted in kits.</span></div></div>
    <div className="form-grid">
      <ItemField id="wb-late-item" label="Item changing to serial-tracked" value={itemId} onChange={setItemId} {...(itemId ? { hint: `Current selection: ${names.item(itemId)}` } : {})} />
      <div className="field"><label htmlFor="wb-late-at">Tracking took effect</label><input id="wb-late-at" className="input" type="datetime-local" value={effectiveAt} max={datetimeInput()} onChange={(event) => setEffectiveAt(event.target.value)} /></div>
      <DocumentField id="wb-late-source" label="Source document" types={["stocktake", "receipt", "production_order", "adjustment"]} value={sourceDocument} onChange={setSourceDocument} {...(sourceDocument ? { hint: names.document(sourceDocument) } : {})} />
      <div className="field"><label htmlFor="wb-late-type">Identifier type</label><select id="wb-late-type" className="select" value={identifierType} onChange={(event) => setIdentifierType(event.target.value)}><option value="internal_serial">Internal serial</option><option value="manufacturer_serial">Manufacturer serial</option><option value="vendor_serial">Vendor serial</option></select></div>
    </div>

    {corrects && <div className="note info"><strong>Reversing an earlier tracking change.</strong>&nbsp;Remove all stock and installed-capture rows; the command preview verifies that no serialised activity happened after it.</div>}
    <div className="correction-subhead"><div><strong>Open stock</strong><span>One serial per physical unit, grouped by location.</span></div><button type="button" className="btn sm" onClick={() => setStock((rows) => [...rows, blankStock(null, "warehouse")])}>Add location</button></div>
    <div className="correction-lines">
      {stock.map((row, index) => <div className="correction-line" key={row.key}>
        <div className="correction-line-title"><span className="slot">Stock location {index + 1}</span><button type="button" className="btn ghost sm" onClick={() => setStock((rows) => rows.filter((candidate) => candidate.key !== row.key))}>Remove</button></div>
        <div className="correction-line-controls late-stock">
          <div className="field"><label htmlFor={`wb-late-location-${row.key}`}>Location reference <span className="hint">· optional for an unassigned bucket</span></label><input id={`wb-late-location-${row.key}`} className="input serial" value={row.locationId ?? ""} onChange={(event) => updateStock(row.key, { locationId: event.target.value.trim() || null })} /></div>
          <div className="field"><label htmlFor={`wb-late-location-kind-${row.key}`}>Location kind</label><select id={`wb-late-location-kind-${row.key}`} className="select" value={row.locationKind} onChange={(event) => updateStock(row.key, { locationKind: event.target.value })}>{LOCATION_KINDS.map((kind) => <option key={kind} value={kind}>{humanise(kind)}</option>)}</select></div>
          <div className="field"><label htmlFor={`wb-late-exempt-${row.key}`}>Exempt quantity</label><input id={`wb-late-exempt-${row.key}`} className="input" type="number" min="0" step="1" value={row.exemptText} onChange={(event) => updateStock(row.key, { exemptText: event.target.value })} /></div>
          <div className="field"><label htmlFor={`wb-late-exempt-reason-${row.key}`}>Exemption reason <span className="hint">· required when exempting</span></label><input id={`wb-late-exempt-reason-${row.key}`} className="input" value={row.exemptReason ?? ""} onChange={(event) => updateStock(row.key, { exemptReason: event.target.value })} /></div>
          <div className="span"><SerialRows id={`late-stock-${row.key}`} label="Serials at this location" values={row.serials} onChange={(serials) => updateStock(row.key, { serials })} /></div>
        </div>
      </div>)}
    </div>

    <div className="correction-subhead"><div><strong>Already fitted in kits</strong><span>Replace an existing quantity component line with the captured serials.</span></div><button type="button" className="btn sm" disabled={itemLinks.length === 0} onClick={() => setKits((rows) => [...rows, blankKit(itemLinks[0]?.linkId ?? "")])}>Add installed capture</button></div>
    {itemLinks.length === 0 && <div className="hint correction-section-hint">No open quantity component lines are available on the loaded unit.</div>}
    <div className="correction-lines">
      {kits.map((row, index) => <div className="correction-line" key={row.key}>
        <div className="correction-line-title"><span className="slot">Installed capture {index + 1}</span><button type="button" className="btn ghost sm" onClick={() => setKits((rows) => rows.filter((candidate) => candidate.key !== row.key))}>Remove</button></div>
        <div className="correction-line-controls">
          <div className="field"><label htmlFor={`wb-late-link-${row.key}`}>Existing component line</label><select id={`wb-late-link-${row.key}`} className="select" value={row.existingLinkId} onChange={(event) => updateKit(row.key, { existingLinkId: event.target.value })}>{itemLinks.map((link) => <option key={link.linkId} value={link.linkId}>{link.slot || "Unslotted"} · {names.item(link.childItemId)}</option>)}</select></div>
          <div className="field"><label htmlFor={`wb-late-fitted-${row.key}`}>Physically fitted</label><input id={`wb-late-fitted-${row.key}`} className="input" type="datetime-local" value={row.fittedAt} max={datetimeInput()} onChange={(event) => updateKit(row.key, { fittedAt: event.target.value })} /></div>
          <div className="span"><SerialRows id={`late-kit-${row.key}`} label="Fitted component serials" values={row.serials} onChange={(serials) => updateKit(row.key, { serials })} /></div>
        </div>
      </div>)}
    </div>
    <Validation errors={errors} />
  </div>;
}

function blankStock(locationId: string | null, locationKind: string): StockUi {
  return { key: crypto.randomUUID(), commissionCommandId: newCommandId(), locationId, locationKind, serials: [""], exemptQuantity: 0, exemptText: "0", exemptReason: null, origin: "purchased" };
}

function blankKit(existingLinkId: string): KitUi {
  return { key: crypto.randomUUID(), commissionCommandId: newCommandId(), existingLinkId, fittedAt: datetimeInput(), serials: [""], origin: "purchased" };
}

function CorrectDocumentSerialsForm({ candidates, primary, initialDocument, reason, onArguments, actor }: CommandFormProps & { actor: string }) {
  const candidateSerials = useMemo(() => candidates.map((row) => row.displayIdentifier).filter((value): value is string => !!value), [candidates]);
  const [document, setDocument] = useState<DocumentValue | null>(initialDocument);
  const [recorded, setRecorded] = useState<string[]>(candidateSerials.length ? candidateSerials : primary ? [primary] : [""]);
  const [physical, setPhysical] = useState<string[]>([""]);
  const [recordedSelection, setRecordedSelection] = useState<string | null>(null);
  const [physicalSelection, setPhysicalSelection] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const reversalOnly = reason === "returned_never_delivered";
  const recordedValues = cleanSerials(recorded);
  const physicalValues = reversalOnly ? [] : cleanSerials(physical);
  const inlineErrors = [
    ...(!document ? ["Choose the posted document whose serials are wrong."] : []),
    ...(recordedValues.length === 0 ? ["Enter at least one serial recorded on the document."] : []),
    ...(!reversalOnly && physicalValues.length === 0 ? ["Enter the serials on the physical units."] : []),
    ...(!reversalOnly && recordedValues.length !== physicalValues.length ? ["Recorded and physical lists must contain the same number of serials."] : []),
    ...(!reversalOnly && recordedValues.some((serial) => physicalValues.some((other) => other.toUpperCase() === serial.toUpperCase())) ? ["Unchanged units belong in neither list; a serial cannot appear on both sides."] : []),
  ];

  useEffect(() => { if (!document && initialDocument) setDocument(initialDocument); }, [initialDocument]); // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => {
    if (candidateSerials.length && cleanSerials(recorded).length === 0) setRecorded(candidateSerials);
  }, [candidateSerials]); // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => {
    if (!document || !recordedSelection || !physicalSelection || inlineErrors.length) { onArguments(null); return; }
    onArguments(buildCorrectDocumentSerialsArguments({ document, recordedSelectionId: recordedSelection, physicalSelectionId: physicalSelection }, context()));
  }, [document, recordedSelection, physicalSelection, reason]); // eslint-disable-line react-hooks/exhaustive-deps

  const dirtyRecorded = (values: string[]) => { setRecorded(values); setRecordedSelection(null); setPhysicalSelection(null); };
  const dirtyPhysical = (values: string[]) => { setPhysical(values); setRecordedSelection(null); setPhysicalSelection(null); };
  const resolve = async () => {
    if (inlineErrors.length) return;
    setBusy(true); setError(null); onArguments(null);
    try {
      const [recordedResult, physicalResult] = await Promise.all([
        selections.resolve({ kind: "pasted_list", serials: recordedValues }, actor),
        selections.resolve({ kind: "pasted_list", serials: physicalValues }, actor),
      ]);
      if (recordedResult.unknown.length || recordedResult.rejected.length) throw new Error(`Recorded list: ${[...recordedResult.unknown, ...recordedResult.rejected].join(", ")} could not be selected.`);
      if (physicalResult.unknown.length || physicalResult.rejected.length) throw new Error(`Physical list: ${[...physicalResult.unknown, ...physicalResult.rejected].join(", ")} could not be selected.`);
      setRecordedSelection(recordedResult.selectionId); setPhysicalSelection(physicalResult.selectionId);
    } catch (reason) { setError(reason); } finally { setBusy(false); }
  };

  return <div className="workbench-command-form">
    <div className="correction-form-head"><div><strong>Correct document serials</strong><span>Reverse the recorded units and apply the same posted document to the physical units; stock quantity does not move.</span></div></div>
    <DocumentField id="wb-document-serials-document" label="Posted document" types={["delivery", "transfer", "receipt"]} value={document} onChange={(value) => { setDocument(value); setRecordedSelection(null); setPhysicalSelection(null); }} />
    {reversalOnly && <div className="note info"><strong>Returned or never delivered.</strong>&nbsp;This reason reverses the recorded side only, so no physical replacement list is required.</div>}
    <div className="correction-serial-pair">
      <SerialRows id="document-recorded" label="Serials recorded on the document" values={recorded} onChange={dirtyRecorded} />
      {!reversalOnly && <SerialRows id="document-physical" label="Serials on the physical units" values={physical} onChange={dirtyPhysical} />}
    </div>
    <Validation errors={inlineErrors} />
    <div className="correction-resolve-bar"><button type="button" className="btn" disabled={busy || inlineErrors.length > 0} aria-busy={busy} onClick={resolve}>{busy ? "Resolving…" : "Resolve serial lists"}</button>{recordedSelection && physicalSelection ? <span className="ok-text">Selections ready · {recordedValues.length} recorded · {physicalValues.length} physical</span> : <span className="hint">Resolve freezes both lists for the command preview.</span>}</div>
    <ErrorNote error={error} />
  </div>;
}

function CorrectSourceDocumentForm({ primary, candidates, lineage, initialDocument, initialSelectionId, onArguments, actor }: CommandFormProps & { actor: string }) {
  const candidateSerials = useMemo(() => candidates.map((row) => row.displayIdentifier).filter((value): value is string => !!value), [candidates]);
  const ownerDocument = lineage?.episodes.find((row) => row.kind === "ownership" && !row.validTo)?.openingDocument ?? null;
  const [previousDocument, setPreviousDocument] = useState<DocumentValue | null>(ownerDocument ?? initialDocument);
  const [correctedDocument, setCorrectedDocument] = useState<DocumentValue | null>(null);
  const [serials, setSerials] = useState<string[]>(candidateSerials.length ? candidateSerials : primary ? [primary] : [""]);
  const [selectionId, setSelectionId] = useState<string | null>(initialSelectionId);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const values = cleanSerials(serials);
  const names = useReferenceNames({ documents: [previousDocument, correctedDocument] });
  const inlineErrors = [
    ...(values.length === 0 && !selectionId ? ["Enter at least one affected unit serial."] : []),
    ...(!previousDocument ? ["Choose the source document currently recorded."] : []),
    ...(!correctedDocument ? ["Choose the source document that should replace it."] : []),
    ...(previousDocument && correctedDocument && sameDocument(previousDocument, correctedDocument) ? ["The current and corrected source documents must differ."] : []),
  ];

  useEffect(() => { if (!previousDocument && (ownerDocument ?? initialDocument)) setPreviousDocument(ownerDocument ?? initialDocument); }, [ownerDocument, initialDocument]); // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => {
    if (!selectionId || !previousDocument || !correctedDocument || inlineErrors.length) { onArguments(null); return; }
    onArguments(buildCorrectSourceDocumentArguments({ selectionId, previousDocument, correctedDocument }, context()));
  }, [selectionId, previousDocument, correctedDocument]); // eslint-disable-line react-hooks/exhaustive-deps

  const resolve = async () => {
    if (!values.length) return;
    setBusy(true); setError(null); onArguments(null);
    try {
      const result = await selections.resolve({ kind: "pasted_list", serials: values }, actor);
      if (result.unknown.length || result.rejected.length) throw new Error(`${[...result.unknown, ...result.rejected].join(", ")} could not be selected.`);
      setSelectionId(result.selectionId);
    } catch (reason) { setError(reason); } finally { setBusy(false); }
  };

  return <div className="workbench-command-form">
    <div className="correction-form-head"><div><strong>Correct the source document</strong><span>Re-point the ownership source, re-derive cover, and request corrected labels without editing history.</span></div></div>
    <div className="correction-source-fields">
      <DocumentField id="wb-source-previous" label="Source currently recorded" types={["sales_order", "delivery"]} value={previousDocument} onChange={setPreviousDocument} {...(previousDocument ? { hint: names.document(previousDocument) } : {})} />
      <DocumentField id="wb-source-corrected" label="Correct source document" types={["sales_order", "delivery"]} value={correctedDocument} onChange={setCorrectedDocument} {...(correctedDocument ? { hint: names.document(correctedDocument) } : {})} />
    </div>
    <SerialRows id="source-units" label="Affected unit serials" values={serials} onChange={(next) => { setSerials(next); setSelectionId(null); }} />
    <Validation errors={inlineErrors} />
    <div className="correction-resolve-bar"><button type="button" className="btn" disabled={busy || values.length === 0} aria-busy={busy} onClick={resolve}>{busy ? "Resolving…" : selectionId ? "Resolve changed list" : "Resolve affected units"}</button>{selectionId ? <span className="ok-text">Selection ready · {values.length || candidates.length} affected unit{(values.length || candidates.length) === 1 ? "" : "s"}</span> : <span className="hint">The selection is frozen before the command preview.</span>}</div>
    <ErrorNote error={error} />
  </div>;
}

function SerialRows({ id, label, values, onChange }: { id: string; label: string; values: string[]; onChange: (value: string[]) => void }) {
  const rows = values.length ? values : [""];
  const update = (index: number, value: string) => onChange(rows.map((row, rowIndex) => rowIndex === index ? value : row));
  const add = () => onChange([...rows, ""]);
  const paste = (event: ClipboardEvent<HTMLInputElement>, index: number) => {
    const incoming = splitSerials(event.clipboardData.getData("text"));
    if (incoming.length <= 1) return;
    event.preventDefault();
    onChange([...rows.slice(0, index), ...incoming, ...rows.slice(index + 1)]);
  };
  return <div className="field correction-serial-list" data-serial-form={id}>
    <div className="correction-list-label"><span className="lbl">{label} <span className="hint">· {cleanSerials(rows).length} entered</span></span><button type="button" className="btn ghost sm" onClick={add}>Add row</button></div>
    {rows.map((value, index) => <div className="correction-serial-row" key={`${id}-${index}`}>
      <span aria-hidden="true" className="correction-row-number">{index + 1}</span>
      <input id={`${id}-${index}`} data-serial-row className="input serial" aria-label={`${label}, row ${index + 1}`} value={value} placeholder="Scan or paste serial" onChange={(event) => update(index, event.target.value)} onPaste={(event) => paste(event, index)} onKeyDown={(event) => focusNextSerial(event, id, add)} />
      <button type="button" className="btn ghost sm" aria-label={`Remove ${label.toLowerCase()} row ${index + 1}`} disabled={rows.length === 1} onClick={() => onChange(rows.filter((_, rowIndex) => rowIndex !== index))}>Remove</button>
    </div>)}
  </div>;
}

function Validation({ errors }: { errors: string[] }) {
  return errors.length ? <div className="note warn correction-validation" role="status"><strong>Complete the form.</strong>&nbsp;{errors[0]}{errors.length > 1 ? ` · ${errors.length - 1} more` : ""}</div> : null;
}

function focusNextSerial(event: KeyboardEvent<HTMLInputElement>, form: string, add: () => void) {
  if (event.key !== "Enter") return;
  event.preventDefault();
  const fields = [...document.querySelectorAll<HTMLInputElement>(`[data-serial-form="${form}"] [data-serial-row]`)];
  const index = fields.indexOf(event.currentTarget);
  if (index >= 0 && index < fields.length - 1) { fields[index + 1]?.focus(); return; }
  add();
  window.requestAnimationFrame(() => {
    const next = document.querySelectorAll<HTMLInputElement>(`[data-serial-form="${form}"] [data-serial-row]`);
    next[next.length - 1]?.focus();
  });
}

function cleanSerials(values: string[]): string[] { return splitSerials(values.join("\n")); }

function context() { return { eventTime: new Date().toISOString() }; }

function datetimeInput(value = new Date().toISOString()): string {
  const date = new Date(value);
  const local = new Date(date.getTime() - date.getTimezoneOffset() * 60_000);
  return local.toISOString().slice(0, 16);
}

function toIso(value: string): string { return new Date(value).toISOString(); }

function sameDocument(first: DocumentValue, second: DocumentValue): boolean { return first.type === second.type && first.id.toLowerCase() === second.id.toLowerCase(); }

const LOCATION_KINDS = ["warehouse", "bin", "customer_site", "in_transit", "returning", "quarantine", "at_subcontractor", "at_3pl", "in_repair", "vendor"];
