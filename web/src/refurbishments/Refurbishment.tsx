import { useCallback, useEffect, useState } from "react";
import { Link, useNavigate, useParams } from "react-router";
import { ApiError } from "../api/client";
import { ActionDialog, PresenceAdvisory, PresenceChips, Shortcut, StatusBadge, useDocumentPresence, useModEnter } from "../components";
import { ItemField, LocationField, PartyField, UnitField, useReferenceNames } from "../corrections/ReferenceFields";
import { usePeople } from "../people/people";
import { Rail } from "../shell/Drawer";
import { Icon } from "../shell/Icons";
import { openDrawer } from "../shell/drawerStore";
import { Breadcrumbs } from "../shell/PageHeader";
import { ErrorNote, Kv, Section, UnitLink } from "../units/common";
import { fmtDateTime } from "../units/format";
import { units, type UnitDetail } from "../api/units";
import { refurbishmentsApi } from "./api";
import { completedOperations, hours, humanise, identityOutcome, identityTitle, money, operationTone, releaseBlocked, refurbishmentTone } from "./format";
import type {
  RefurbishmentDialog, RefurbishmentOperationRow, RefurbishmentWorkOrderRow, SanitisationRow,
  UnitAttributeHistoryRow, UnitTestRow, UsageReadingRow,
} from "./types";
import "./refurbishments.css";

const GRADES = [
  { value: "refurbished_a", label: "Refurbished A", detail: "as new · light marks only" },
  { value: "refurbished_b", label: "Refurbished B", detail: "visible wear" },
  { value: "refurbished_c", label: "Refurbished C", detail: "heavy wear · fully working" },
] as const;

export function Component() {
  const { id = "" } = useParams();
  const navigate = useNavigate();
  const [row, setRow] = useState<RefurbishmentWorkOrderRow | null>(null);
  const [unit, setUnit] = useState<UnitDetail | null>(null);
  const [sanitisation, setSanitisation] = useState<SanitisationRow[]>([]);
  const [tests, setTests] = useState<UnitTestRow[]>([]);
  const [usage, setUsage] = useState<UsageReadingRow[]>([]);
  const [attributes, setAttributes] = useState<UnitAttributeHistoryRow[]>([]);
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [dialog, setDialog] = useState<RefurbishmentDialog>(null);
  const [completing, setCompleting] = useState<RefurbishmentOperationRow | null>(null);
  const [grade, setGrade] = useState("refurbished_a");

  const load = useCallback(async (signal?: AbortSignal) => {
    setLoading(true); setError(null);
    try {
      let detail: RefurbishmentWorkOrderRow;
      try { detail = await refurbishmentsApi.detail(id, signal); }
      catch (reason) {
        if (!(reason instanceof ApiError && reason.status === 404)) throw reason;
        const matches = await refurbishmentsApi.forUnit(id, signal);
        const match = matches.items.find((candidate) => candidate.status === "open") ?? matches.items[0];
        if (!match) throw reason;
        navigate(`/refurbishments/${match.workOrderId}`, { replace: true }); return;
      }
      setRow(detail); setGrade(detail.grade ?? "refurbished_a");
      const [nextUnit, nextSanitisation, nextTests, nextUsage, nextAttributes] = await Promise.all([
        units.detail(detail.targetUnitId, signal), refurbishmentsApi.sanitisation(detail.targetUnitId, signal),
        refurbishmentsApi.tests(detail.targetUnitId, signal), refurbishmentsApi.usage(detail.targetUnitId, signal),
        refurbishmentsApi.attributes(detail.targetUnitId, signal),
      ]);
      setUnit(nextUnit); setSanitisation(nextSanitisation); setTests(nextTests); setUsage(nextUsage); setAttributes(nextAttributes);
    } catch (reason) { if (!(reason instanceof DOMException && reason.name === "AbortError")) setError(reason); }
    finally { setLoading(false); }
  }, [id, navigate]);
  useEffect(() => { const controller = new AbortController(); void load(controller.signal); return () => controller.abort(); }, [load]);

  if (loading && !row) return <main className="content"><Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Refurbishment", to: "/refurbishments" }, { label: "Loading…" }]} /><p className="muted">Loading refurbishment work order…</p></main>;
  if (!row) return <main className="content"><Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Refurbishment", to: "/refurbishments" }, { label: "Work order" }]} /><ErrorNote error={error ?? new Error("Refurbishment work order not found.")} /></main>;

  return <RefurbishmentScreen row={row} unit={unit} sanitisation={sanitisation} tests={tests} usage={usage} attributes={attributes}
    grade={grade} setGrade={setGrade} dialog={dialog} setDialog={setDialog} completing={completing} setCompleting={setCompleting}
    busy={busy} setBusy={setBusy} error={error} setError={setError} reload={() => load()} />;
}

function RefurbishmentScreen({ row, unit, sanitisation, tests, usage, attributes, grade, setGrade, dialog, setDialog, completing, setCompleting, busy, setBusy, error, setError, reload }: {
  row: RefurbishmentWorkOrderRow; unit: UnitDetail | null; sanitisation: SanitisationRow[]; tests: UnitTestRow[]; usage: UsageReadingRow[]; attributes: UnitAttributeHistoryRow[];
  grade: string; setGrade: (value: string) => void; dialog: RefurbishmentDialog; setDialog: (value: RefurbishmentDialog) => void;
  completing: RefurbishmentOperationRow | null; setCompleting: (value: RefurbishmentOperationRow | null) => void;
  busy: boolean; setBusy: (value: boolean) => void; error: unknown; setError: (value: unknown) => void; reload: () => Promise<void>;
}) {
  const editing = dialog !== null || completing !== null;
  const presence = useDocumentPresence("refurbishment_work_order", row.workOrderId, editing);
  const currentParts = unit?.asBuilt.filter((part) => part.removedAt === null) ?? [];
  const itemIds = [row.itemId, ...row.parts.map((part) => part.childItemId), ...currentParts.map((part) => part.childItemId)];
  const partyIds = row.parts.map((part) => part.vendorPartyRef);
  const names = useReferenceNames({ items: itemIds, parties: partyIds });
  const people = usePeople([row.openedBy, row.releasedBy, ...row.operations.flatMap((operation) => [operation.technicianRef, operation.signedBy]), ...row.labour.map((labour) => labour.technicianRef), ...sanitisation.map((record) => record.operatorRef), ...tests.map((record) => record.operatorRef), ...usage.map((record) => record.operatorRef), ...attributes.map((record) => record.actorRef)]);
  const totalHours = row.labour.reduce((sum, labour) => sum + labour.hours, 0);
  const labourCost = row.labour.reduce((sum, labour) => sum + labour.amount, 0);
  const openChecks = row.releaseChecks.filter((check) => check.required && !check.passed).length;
  const outcome = identityOutcome(row.identityPolicy, row.targetSerial, row.releasedSerial, row.status === "released");
  const openOperation = (operation: RefurbishmentOperationRow) => openDrawer({ eyebrow: "Operation", title: operation.description }, <Section title="Operation"><Kv rows={[["Sequence", operation.sequence], ["Assigned to", people(operation.technicianRef)], ["Planned", hours(operation.plannedHours)], ["Actual", operation.actualHours === null ? "—" : hours(operation.actualHours)], ["Status", humanise(operation.status)], ["Signed by", people(operation.signedBy)], ["Signed at", fmtDateTime(operation.signedAt)]]} /></Section>);
  const releaseIsBlocked = releaseBlocked(row.releaseChecks);
  // The page shortcut opens the release dialog. The dialog owns its separate confirmation action.
  useModEnter(() => setDialog("release"), row.status !== "open" || releaseIsBlocked);

  return <>
    <main className="content" id="main" tabIndex={-1}>
      <Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Refurbishment", to: "/refurbishments" }, { label: row.workOrderNumber }]} />
      <div className="page-header"><div><div className="eyebrow">Refurbishment work order {row.workOrderNumber} · {humanise(row.kind)} · {humanise(row.status)} · opened {fmtDateTime(row.openedAt)} by {people(row.openedBy)} · cycle {row.cycleNumber} of {row.maxCycles}</div><h1>{row.targetSerial ?? "Serial unavailable"} · {names.item(row.itemId)}</h1><p className="sub">A work order against the existing unit, never a production order · parts and labour roll into this unit’s cost · release applies {identityTitle(row.identityPolicy).toLowerCase()}</p><PresenceChips users={presence} /></div>{row.status === "open" && <div className="actions"><button type="button" className="btn" onClick={() => setDialog("labour")}>Record labour</button><button type="button" className="btn" onClick={() => setDialog("cost")}>Record cost</button><button type="button" className="btn" onClick={() => setDialog("cancel")}>Cancel…</button><button type="button" className="btn primary" disabled={releaseIsBlocked} title={releaseIsBlocked ? "Complete the server release checks first" : undefined} onClick={() => setDialog("release")}><Icon name="check" />Release<Shortcut keys="mod+enter" /></button></div>}</div>
      <PresenceAdvisory users={presence} editing={editing} documentLabel="refurbishment work order" />
      <ErrorNote error={error} onReload={() => { setDialog(null); setCompleting(null); void reload(); }} />
      <div className="stack">
        <div className="refurb-summary" aria-label="Work order figures"><span><span className="k">Operations</span><span className="v">{completedOperations(row)} of {row.operations.length}</span></span><span><span className="k">Parts</span><span className="v">{row.parts.length}</span></span><span><span className="k">Labour</span><span className="v">{hours(totalHours)}</span><span className="sub">{money(labourCost, row.currency)}</span></span><span><span className="k">Release checks</span><span className={`v${openChecks ? " warn-text" : " ok-text"}`}>{openChecks ? `${openChecks} open` : "Ready"}</span></span></div>
        <div className="cols-3-2">
          <div className="stack">
            <section className="card" aria-labelledby="refurb-operations"><div className="card-header"><h2 id="refurb-operations">Operations</h2>{row.status === "open" && <button type="button" className="btn sm" onClick={() => setDialog("add_operation")}><Icon name="plus" />Add operation</button>}</div><table className="table"><colgroup><col style={{ width: "8%" }} /><col style={{ width: "42%" }} /><col style={{ width: "20%" }} /><col style={{ width: "12%" }} /><col style={{ width: "18%" }} /></colgroup><thead><tr><th>#</th><th>Operation</th><th>By</th><th className="num">Hours</th><th>Status</th></tr></thead><tbody>{row.operations.map((operation) => <tr key={operation.operationId} className="clickable" tabIndex={0} onClick={() => openOperation(operation)} onKeyDown={(event) => { if (event.key === "Enter") openOperation(operation); }}><td>{operation.sequence}</td><td className="primary"><span className="t">{operation.description}</span><span className="sub">planned {hours(operation.plannedHours)}</span></td><td>{people(operation.signedBy ?? operation.technicianRef)}</td><td className="num">{operation.actualHours === null ? "—" : hours(operation.actualHours)}</td><td><StatusBadge tone={operationTone(operation.status)}>{humanise(operation.status)}</StatusBadge>{row.status === "open" && operation.status === "open" && <button type="button" className="btn ghost sm" onClick={(event) => { event.stopPropagation(); setCompleting(operation); }}>Complete…</button>}</td></tr>)}</tbody></table>{row.operations.length === 0 && <div className="card-body muted">No operations recorded.</div>}</section>

            <section className="card" aria-labelledby="refurb-parts"><div className="card-header"><h2 id="refurb-parts">Parts removed and installed</h2>{row.status === "open" && <div className="actions"><button type="button" className="btn sm" disabled={!currentParts.length} onClick={() => setDialog("remove_part")}>Remove part…</button><button type="button" className="btn sm" onClick={() => setDialog("install_part")}>Install part…</button></div>}</div><table className="table"><colgroup><col style={{ width: "16%" }} /><col style={{ width: "30%" }} /><col style={{ width: "18%" }} /><col style={{ width: "20%" }} /><col style={{ width: "16%" }} /></colgroup><thead><tr><th>Action</th><th>Part</th><th>Slot</th><th>Destination</th><th className="num">Cost</th></tr></thead><tbody>{row.parts.map((part) => <tr key={part.partId}><td><StatusBadge tone={part.action === "installed" ? "ok" : "neutral"}>{humanise(part.action)}</StatusBadge></td><td>{part.childUnitId ? <UnitLink unitId={part.childUnitId}>{serialForChild(unit, part.childUnitId) ?? "Open unit"}</UnitLink> : names.item(part.childItemId)}</td><td>{part.slot ?? "—"}</td><td>{part.vendorPartyRef ? `${humanise(part.destination)} · ${names.party(part.vendorPartyRef)}` : humanise(part.destination)}</td><td className="num">{part.unitCost === null ? "—" : money(part.unitCost, part.currency)}</td></tr>)}</tbody></table>{row.parts.length === 0 && <div className="card-body muted">No part changes recorded.</div>}</section>

            <section className="card" aria-labelledby="refurb-release"><div className="card-header"><h2 id="refurb-release">Release</h2><span className="meta">what the unit becomes · decided by server policy</span></div><div className="card-body"><div className="refurb-grades" role="radiogroup" aria-label="Grade on release">{GRADES.map((option) => <label key={option.value} className={grade === option.value ? "on" : ""}><input type="radio" name="release-grade" value={option.value} checked={grade === option.value} disabled={row.status !== "open"} onChange={() => setGrade(option.value)} /><span>{option.label}<span className="s">{option.detail}</span></span></label>)}</div><div className="refurb-pair"><div className="side"><div className="k">Serial now</div><span className="serial">{outcome.before}</span></div><div className="arrow" aria-hidden="true">→</div><div className="side"><div className="k">{row.identityPolicy === "new_unit" ? "Derived unit after release" : "Serial after release"}</div><span className="serial">{outcome.after}</span></div></div><div className="note ok" style={{ marginTop: 12 }}><strong>{identityTitle(row.identityPolicy)}</strong>&nbsp;{outcome.detail}</div></div></section>
          </div>

          <div className="stack">
            <section className="card" aria-labelledby="refurb-checks"><div className="card-header"><h2 id="refurb-checks">Release checks</h2><span className="meta">returned by the server</span></div><div className="card-body"><ul className="refurb-checks">{row.releaseChecks.map((check) => <li key={check.key}><span>{check.label}<span className="s">{check.detail}</span></span><StatusBadge tone={check.passed ? "ok" : check.required ? "warn" : "neutral"}>{check.passed ? check.required ? "Ready" : "Not required" : "Open"}</StatusBadge></li>)}</ul></div></section>
            <ConditionPanels sanitisation={sanitisation} tests={tests} usage={usage} attributes={attributes} people={people} />
            <section className="card" aria-labelledby="refurb-record"><div className="card-header"><h2 id="refurb-record">{row.status === "released" ? "Recorded on release" : "Will be recorded on release"}</h2></div><table className="table dense"><tbody><tr><td className="primary">Refurbishment.Released</td><td>{humanise(grade)} · cycle {row.cycleNumber}</td></tr><tr><td className="primary">Unit.Graded</td><td>{humanise(grade)} · valuation rule entered at release</td></tr><tr><td className="primary">Identity</td><td>{identityTitle(row.identityPolicy)}</td></tr><tr><td className="primary">Custody</td><td>In repair → warehouse</td></tr></tbody></table></section>
          </div>
        </div>
      </div>
    </main>
    <Rail label="Refurbishment work order"><Section title="Work order"><Kv rows={[["Number", row.workOrderNumber], ["Status", humanise(row.status)], ["Kind", humanise(row.kind)], ["Opened", fmtDateTime(row.openedAt)], ["Opened by", people(row.openedBy)], ["Identity policy", identityTitle(row.identityPolicy)], ["Cycle", `${row.cycleNumber} of ${row.maxCycles}`], ["Total cost", money(row.totalCost, row.currency)]]} /></Section><Section title="Unit"><div className="act"><Link className="btn block" to={`/units/${row.targetUnitId}`}>Open unit history</Link></div></Section></Rail>
    {dialog && <RefurbishmentActionDialog kind={dialog} row={row} unit={unit} grade={grade} names={names} busy={busy} onCancel={() => setDialog(null)} onDone={async () => { setDialog(null); await reload(); }} setBusy={setBusy} setError={setError} />}
    {completing && <CompleteOperationDialog row={row} operation={completing} busy={busy} onCancel={() => setCompleting(null)} onDone={async () => { setCompleting(null); await reload(); }} setBusy={setBusy} setError={setError} />}
  </>;
}

function ConditionPanels({ sanitisation, tests, usage, attributes, people }: { sanitisation: SanitisationRow[]; tests: UnitTestRow[]; usage: UsageReadingRow[]; attributes: UnitAttributeHistoryRow[]; people: ReturnType<typeof usePeople> }) {
  const clean = sanitisation[0];
  return <>{clean && <section className="card" aria-labelledby="refurb-san"><div className="card-header"><h2 id="refurb-san">Sanitisation</h2><span className="meta">latest record</span></div><div className="card-body"><Kv rows={[["Method", humanise(clean.method)], ["Media", `${clean.manufacturer} · ${clean.model} · ${clean.mediaType}`], ["Tool", `${clean.tool}${clean.toolVersion ? ` ${clean.toolVersion}` : ""}`], ["Verified", clean.verificationMethod], ["By", `${people(clean.operatorRef)} · ${fmtDateTime(clean.sanitisedAt)}`]]} /></div></section>}
    {(attributes.length > 0 || usage.length > 0) && <section className="card" aria-labelledby="refurb-attrs"><div className="card-header"><h2 id="refurb-attrs">Attributes and readings</h2><span className="meta">history kept</span></div><table className="table dense"><tbody>{attributes.slice(0, 4).map((entry) => <tr key={entry.attributeHistoryId}><td>{humanise(entry.key)}</td><td>{entry.value ?? "—"}<span className="sub">{fmtDateTime(entry.effectiveFrom)} · {people(entry.actorRef)}</span></td></tr>)}{usage.slice(0, 3).map((entry) => <tr key={entry.usageReadingId}><td>{humanise(entry.meterKind)}</td><td>{entry.value.toLocaleString()} {entry.unit}<span className="sub">{fmtDateTime(entry.readAt)} · {people(entry.operatorRef)}</span></td></tr>)}</tbody></table></section>}
    {tests.length > 0 && <section className="card" aria-labelledby="refurb-tests"><div className="card-header"><h2 id="refurb-tests">Tests</h2></div><table className="table dense"><tbody>{tests.slice(0, 4).map((test) => <tr key={test.testRecordId}><td>{humanise(test.testKind)}</td><td><StatusBadge tone={test.result === "pass" ? "ok" : "bad"}>{humanise(test.result)}</StatusBadge><span className="sub">{test.equipment} · {fmtDateTime(test.testedAt)} by {people(test.operatorRef)}</span></td></tr>)}</tbody></table></section>}
  </>;
}

function RefurbishmentActionDialog({ kind, row, unit, grade, names, busy, onCancel, onDone, setBusy, setError }: {
  kind: Exclude<RefurbishmentDialog, null>; row: RefurbishmentWorkOrderRow; unit: UnitDetail | null; grade: string;
  names: ReturnType<typeof useReferenceNames>; busy: boolean; onCancel: () => void; onDone: () => Promise<void>;
  setBusy: (value: boolean) => void; setError: (value: unknown) => void;
}) {
  const openParts = unit?.asBuilt.filter((part) => part.removedAt === null) ?? [];
  const [sequence, setSequence] = useState(String((row.operations.at(-1)?.sequence ?? 0) + 10));
  const [description, setDescription] = useState(""); const [plannedHours, setPlannedHours] = useState("0.5");
  const [assemblyLinkId, setAssemblyLinkId] = useState(openParts[0]?.linkId ?? ""); const [destination, setDestination] = useState("spares"); const [vendor, setVendor] = useState<string | null>(null);
  const [installMode, setInstallMode] = useState<"unit" | "item">("unit"); const [childUnitId, setChildUnitId] = useState<string | null>(null); const [childItemId, setChildItemId] = useState<string | null>(null); const [quantity, setQuantity] = useState("1"); const [slot, setSlot] = useState(""); const [unitCost, setUnitCost] = useState("0");
  const [operationId, setOperationId] = useState<string | null>(null); const [labourHours, setLabourHours] = useState("1"); const [rate, setRate] = useState("0");
  const [currency, setCurrency] = useState(row.currency && row.currency !== "XXX" ? row.currency : "GBP");
  const [costKind, setCostKind] = useState("other"); const [costAmount, setCostAmount] = useState(""); const [costDescription, setCostDescription] = useState("");
  const [valuationRule, setValuationRule] = useState("refurbishment_release"); const [reason, setReason] = useState("");
  // Release and cancellation put the unit into stock, so the destination is a warehouse location the user picks, never the repair bench it sits on.
  const [warehouseId, setWarehouseId] = useState<string | null>(null);
  const title = { add_operation: "Add operation", remove_part: "Remove part", install_part: "Install part", labour: "Record labour", cost: "Record cost", release: "Release refurbishment", cancel: "Cancel work order" }[kind];
  const valid = kind === "add_operation" ? Number(sequence) > 0 && !!description.trim() && Number(plannedHours) >= 0
    : kind === "remove_part" ? !!assemblyLinkId && (destination !== "vendor_return" || !!vendor)
    : kind === "install_part" ? (installMode === "unit" ? !!childUnitId : !!childItemId && Number(quantity) > 0) && Number(unitCost) >= 0 && currency.length === 3
    : kind === "labour" ? Number(labourHours) > 0 && Number(rate) >= 0 && currency.length === 3
    : kind === "cost" ? Number(costAmount) >= 0 && !!costDescription.trim() && currency.length === 3
    : kind === "release" ? !!warehouseId && !releaseBlocked(row.releaseChecks) && !!valuationRule.trim()
    : !!reason.trim() && !!warehouseId;
  const submit = async () => {
    if (!valid) return; setBusy(true); setError(null);
    try {
      if (kind === "add_operation") await refurbishmentsApi.addOperation(row.workOrderId, Number(sequence), description.trim(), Number(plannedHours));
      else if (kind === "remove_part") await refurbishmentsApi.removePart(row.workOrderId, assemblyLinkId, destination, row.currentLocationId, vendor);
      else if (kind === "install_part") await refurbishmentsApi.installPart(row.workOrderId, { childUnitId: installMode === "unit" ? childUnitId : null, childItemId: installMode === "item" ? childItemId : null, quantity: installMode === "item" ? Number(quantity) : 1, relation: "component", slot: slot.trim() || null }, Number(unitCost), currency.toUpperCase(), row.currentLocationId);
      else if (kind === "labour") await refurbishmentsApi.labour(row.workOrderId, operationId, Number(labourHours), Number(rate), currency.toUpperCase());
      else if (kind === "cost") await refurbishmentsApi.cost(row.workOrderId, costKind, Number(costAmount), currency.toUpperCase(), costDescription.trim());
      else if (kind === "release") await refurbishmentsApi.release(row.workOrderId, grade, valuationRule.trim(), warehouseId!);
      else await refurbishmentsApi.cancel(row.workOrderId, reason.trim(), warehouseId!);
      await onDone();
    } catch (reason) { setError(reason); } finally { setBusy(false); }
  };
  return <ActionDialog title={title} wide={kind === "install_part" || kind === "release"} onCancel={onCancel} actions={<button type="button" className="btn primary" disabled={busy || !valid} onClick={() => void submit()}>{busy ? "Recording…" : title}</button>}><div className="refurb-form-grid">
    {kind === "add_operation" && <><div className="field"><label htmlFor="operation-sequence">Sequence</label><input id="operation-sequence" className="input" type="number" min="1" step="10" value={sequence} onChange={(event) => setSequence(event.target.value)} /></div><div className="field"><label htmlFor="operation-hours">Planned hours</label><input id="operation-hours" className="input" type="number" min="0" step="0.25" value={plannedHours} onChange={(event) => setPlannedHours(event.target.value)} /></div><div className="field span"><label htmlFor="operation-description">Operation</label><textarea id="operation-description" className="textarea" autoFocus value={description} onChange={(event) => setDescription(event.target.value)} /></div></>}
    {kind === "remove_part" && <><div className="field span"><label htmlFor="remove-part">Part in the current as-built</label><select id="remove-part" className="select" value={assemblyLinkId} onChange={(event) => setAssemblyLinkId(event.target.value)}>{openParts.map((part) => <option key={part.linkId} value={part.linkId}>{part.childSerial ?? names.item(part.childItemId)}{part.slot ? ` · ${part.slot}` : ""}</option>)}</select></div><div className="field span"><label htmlFor="part-destination">Removed part goes to</label><select id="part-destination" className="select" value={destination} onChange={(event) => { setDestination(event.target.value); if (event.target.value !== "vendor_return") setVendor(null); }}><option value="spares">Spares</option><option value="scrap">Scrap</option><option value="vendor_return">Vendor return</option></select></div>{destination === "vendor_return" && <div className="span"><PartyField id="part-vendor" label="Vendor" value={vendor} onChange={setVendor} /></div>}</>}
    {kind === "install_part" && <><div className="field span"><span className="lbl">Part identity</span><div className="segmented plain" role="group" aria-label="Part identity"><button type="button" aria-pressed={installMode === "unit"} onClick={() => setInstallMode("unit")}>Serialised unit</button><button type="button" aria-pressed={installMode === "item"} onClick={() => setInstallMode("item")}>Item and quantity</button></div></div><div className="span">{installMode === "unit" ? <UnitField id="installed-unit" label="Unit to install" value={childUnitId} onChange={setChildUnitId} /> : <ItemField id="installed-item" label="Item to install" value={childItemId} onChange={setChildItemId} />}</div>{installMode === "item" && <div className="field"><label htmlFor="installed-quantity">Quantity</label><input id="installed-quantity" className="input" type="number" min="0.001" step="0.001" value={quantity} onChange={(event) => setQuantity(event.target.value)} /></div>}<div className="field"><label htmlFor="installed-slot">Slot <span className="hint">· optional</span></label><input id="installed-slot" className="input" value={slot} onChange={(event) => setSlot(event.target.value)} /></div><div className="field"><label htmlFor="installed-cost">Unit cost</label><input id="installed-cost" className="input" type="number" min="0" step="0.01" value={unitCost} onChange={(event) => setUnitCost(event.target.value)} /></div><CurrencyField value={currency} onChange={setCurrency} /></>}
    {kind === "labour" && <><div className="field span"><label htmlFor="labour-operation">Operation <span className="hint">· optional</span></label><select id="labour-operation" className="select" value={operationId ?? ""} onChange={(event) => setOperationId(event.target.value || null)}><option value="">General work order labour</option>{row.operations.map((operation) => <option key={operation.operationId} value={operation.operationId}>{operation.sequence} · {operation.description}</option>)}</select></div><div className="field"><label htmlFor="labour-hours">Hours</label><input id="labour-hours" className="input" type="number" min="0.01" step="0.25" value={labourHours} onChange={(event) => setLabourHours(event.target.value)} /></div><div className="field"><label htmlFor="labour-rate">Hourly rate</label><input id="labour-rate" className="input" type="number" min="0" step="0.01" value={rate} onChange={(event) => setRate(event.target.value)} /></div><CurrencyField value={currency} onChange={setCurrency} /></>}
    {kind === "cost" && <><div className="field"><label htmlFor="cost-kind">Cost kind</label><select id="cost-kind" className="select" value={costKind} onChange={(event) => setCostKind(event.target.value)}><option value="other">Other</option><option value="graded_receipt">Graded receipt value</option></select></div><div className="field"><label htmlFor="cost-amount">Amount</label><input id="cost-amount" className="input" type="number" min="0" step="0.01" value={costAmount} onChange={(event) => setCostAmount(event.target.value)} /></div><CurrencyField value={currency} onChange={setCurrency} /><div className="field span"><label htmlFor="cost-description">Description</label><textarea id="cost-description" className="textarea" value={costDescription} onChange={(event) => setCostDescription(event.target.value)} /></div></>}
    {kind === "release" && <><div className="field span"><div className="note ok"><strong>{humanise(grade)}</strong>&nbsp;{identityTitle(row.identityPolicy)}. The server will allocate a serial when the policy requires one.</div></div><div className="span"><LocationField id="release-location" label="Released into" value={warehouseId} onChange={setWarehouseId} kind="warehouse" hint="Custody moves from the repair bench to this warehouse location" /></div><div className="field span"><label htmlFor="valuation-rule">Valuation rule</label><input id="valuation-rule" className="input" value={valuationRule} onChange={(event) => setValuationRule(event.target.value)} /><span className="help">Recorded with Unit.Graded for downstream valuation.</span></div><div className="field span"><span className="lbl">Server release checks</span><ul className="refurb-checks">{row.releaseChecks.map((check) => <li key={check.key}><span>{check.label}<span className="s">{check.detail}</span></span><StatusBadge tone={check.passed ? "ok" : "warn"}>{check.passed ? "Ready" : "Open"}</StatusBadge></li>)}</ul></div></>}
    {kind === "cancel" && <div className="field span"><label htmlFor="cancel-reason">Reason</label><textarea id="cancel-reason" className="textarea" autoFocus value={reason} onChange={(event) => setReason(event.target.value)} /><span className="help">Cancellation returns the unit to stock.</span></div>}{kind === "cancel" && <div className="span"><LocationField id="cancel-location" label="Return to" value={warehouseId} onChange={setWarehouseId} kind="warehouse" /></div>}
  </div></ActionDialog>;
}

function CompleteOperationDialog({ row, operation, busy, onCancel, onDone, setBusy, setError }: { row: RefurbishmentWorkOrderRow; operation: RefurbishmentOperationRow; busy: boolean; onCancel: () => void; onDone: () => Promise<void>; setBusy: (value: boolean) => void; setError: (value: unknown) => void }) {
  const [actualHours, setActualHours] = useState(String(operation.plannedHours)); const valid = Number(actualHours) >= 0;
  const submit = async () => { if (!valid) return; setBusy(true); setError(null); try { await refurbishmentsApi.completeOperation(row.workOrderId, operation.operationId, Number(actualHours)); await onDone(); } catch (reason) { setError(reason); } finally { setBusy(false); } };
  return <ActionDialog title="Complete operation" onCancel={onCancel} actions={<button type="button" className="btn primary" disabled={busy || !valid} onClick={() => void submit()}>Complete and sign</button>}><p><strong>{operation.sequence} · {operation.description}</strong></p><div className="field"><label htmlFor="actual-hours">Actual hours</label><input id="actual-hours" className="input" type="number" min="0" step="0.25" value={actualHours} onChange={(event) => setActualHours(event.target.value)} /><span className="help">The server stamps the signed-in technician and completion time.</span></div></ActionDialog>;
}

function CurrencyField({ value, onChange }: { value: string; onChange: (value: string) => void }) { return <div className="field"><label htmlFor="refurb-currency">Currency</label><input id="refurb-currency" className="input" maxLength={3} value={value} onChange={(event) => onChange(event.target.value.toUpperCase())} /></div>; }
function serialForChild(unit: UnitDetail | null, childUnitId: string): string | null { return unit?.asBuilt.find((part) => part.childUnitId === childUnitId)?.childSerial ?? null; }
