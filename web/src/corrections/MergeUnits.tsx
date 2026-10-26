import { useEffect, useMemo, useState } from "react";
import { Link, useNavigate, useSearchParams } from "react-router";
import { units, newCommandId, now, type IdentifierRow, type UnitDetail } from "../api/units";
import { DataGrid, Shortcut, StatusBadge, useModEnter, type GridColumn } from "../components";
import { usePeople } from "../people/people";
import { useSession } from "../session/session";
import { Rail } from "../shell/Drawer";
import { openDrawer } from "../shell/drawerStore";
import { opaqueParam } from "../shell/handoff";
import { Breadcrumbs } from "../shell/PageHeader";
import { ErrorNote, Kv, Section, UnitLink } from "../units/common";
import { fmtDate, humanise, shortId, statusLabel } from "../units/format";
import { correctionsApi } from "./api";
import { GUID } from "./format";
import { useReferenceNames } from "./ReferenceFields";
import { workbenchApi } from "./workbenchApi";
import type { MergeUnits, MergeUnitsPreviewResult, MergeUnitsResult } from "./types";

interface IdentifierView extends IdentifierRow { record: "A" | "B"; unitId: string; after: string }

export function Component() {
  const session = useSession();
  const navigate = useNavigate();
  const [params] = useSearchParams();
  // Links hand over unit ids only (?unit=, ?first=, ?second=); a person types or scans a serial, asset tag or unit id.
  const [firstId, setFirstId] = useState(opaqueParam(params, "first") || opaqueParam(params, "unit"));
  const [secondId, setSecondId] = useState(opaqueParam(params, "second"));
  const [records, setRecords] = useState<{ first: UnitDetail; second: UnitDetail } | null>(null);
  const [survivorId, setSurvivorId] = useState("");
  const [reason, setReason] = useState("vendor_serial_keyed_twice");
  const [note, setNote] = useState("");
  const [commandId, setCommandId] = useState(newCommandId);
  const [preview, setPreview] = useState<MergeUnitsPreviewResult | null>(null);
  const [committed, setCommitted] = useState<MergeUnitsResult | null>(null);
  const [active, setActive] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const peopleRefs = records ? [...records.first.identifiers, ...records.second.identifiers].map((row) => row.actorRef) : [];
  const person = usePeople(peopleRefs);

  const load = async () => {
    if (!firstId.trim() || !secondId.trim() || firstId.trim() === secondId.trim()) return;
    setBusy(true); setError(null); setPreview(null); setCommitted(null);
    try {
      const [a, b] = await Promise.all([unitIdFor(firstId), unitIdFor(secondId)]);
      if (a === b) throw new Error("Both entries resolve to the same unit.");
      const [first, second] = await Promise.all([units.detail(a), units.detail(b)]); setRecords({ first, second }); setSurvivorId(first.unitId);
    } catch (reason) { setError(reason); } finally { setBusy(false); }
  };
  useEffect(() => { if (firstId && secondId && firstId !== secondId) void load(); }, []); // route parameters are an initial handoff from the workbench

  const runPreview = async () => {
    if (!records || !survivorId) return; setBusy(true); setError(null); setCommitted(null);
    try { const result = await correctionsApi.previewMerge({ firstUnitId: records.first.unitId, secondUnitId: records.second.unitId, eventTime: now(), survivorUnitId: survivorId }); setPreview(result); if (!survivorId) setSurvivorId(result.recommendedSurvivorUnitId); } catch (reason) { setError(reason); } finally { setBusy(false); }
  };
  const command = useMemo<MergeUnits | null>(() => !records || !survivorId ? null : { commandId, survivorUnitId: survivorId, absorbedUnitId: survivorId === records.first.unitId ? records.second.unitId : records.first.unitId, reason, eventTime: now(), actorRef: "", resolution: "merge", vendorSerialValue: null, note: note || null }, [records, survivorId, commandId, reason, note]);
  const commit = async () => { if (!command || !preview || preview.approval.status !== "allowed" || preview.preview.block > 0 || preview.preview.refusal) return; setBusy(true); setError(null); try { const result = await correctionsApi.merge(command); setCommitted(result); setCommandId(newCommandId()); } catch (reason) { setError(reason); } finally { setBusy(false); } };

  const identifiers = useMemo<IdentifierView[]>(() => {
    if (!records) return [];
    const convert = (unit: UnitDetail, record: "A" | "B") => unit.identifiers.map((row) => ({ ...row, unitId: unit.unitId, record, after: unit.unitId === survivorId ? "current · kept on survivor" : "merged alias · resolves to survivor" }));
    return [...convert(records.first, "A"), ...convert(records.second, "B")];
  }, [records, survivorId]);
  const openIdentifier = (row: IdentifierView) => { setActive(row.identifierId); openDrawer({ eyebrow: `Identifier · record ${row.record}`, title: row.rawValue, mono: true }, <><Kv rows={[["Type", row.type], ["Scheme", row.scheme], ["Valid from", fmtDate(row.validFrom)], ["After merge", row.after], ["Captured by", person(row.actorRef)], ["Lookups", "resolve to the survivor"]]} /><div className="act"><UnitLink className="btn block" unitId={row.unitId}>Open current record</UnitLink></div></>); };
  const columns: GridColumn<IdentifierView>[] = [
    { key: "value", label: "Value", width: 24, mono: true, sort: { key: "value", value: (row) => row.rawValue }, render: (row) => row.rawValue },
    { key: "type", label: "Type", width: 20, sort: { key: "type", value: (row) => row.type }, render: (row) => row.type },
    { key: "from", label: "From", width: 18, sort: { key: "from", value: (row) => row.record }, render: (row) => `Record ${row.record}` },
    { key: "after", label: "After", width: 38, sort: { key: "after", value: (row) => row.after }, render: (row) => row.after },
  ];
  const approval = preview?.approval;
  const needsApproval = approval?.status === "needs_approval";
  const approvalPath = `/corrections?choice=duplicate_unit${records ? `&unit=${records.first.unitId}` : ""}`;
  const commitDisabled = !command || !preview || approval?.status !== "allowed" || preview.preview.block > 0 || busy;
  useModEnter(() => needsApproval ? navigate(approvalPath) : commit(), needsApproval ? false : commitDisabled);

  return <>
    <main className="content" id="main" tabIndex={-1}>
      <Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Corrections", to: "/corrections" }, { label: "Merge units" }]} />
      <div className="page-header"><div><div className="eyebrow">Correction · two records are the same physical unit · nothing is deleted</div><h1>Merge units</h1><p className="sub">Choose the survivor. Every event, identifier, episode, entitlement and link of the other record moves to it with a record of where it came from; the absorbed record stays readable as an alias so outside references still resolve.</p></div><div className="actions"><Link className="btn ghost" to="/corrections">Back to workbench</Link><button type="button" className="btn" disabled={!records || busy} onClick={() => void runPreview()}>Preview merge</button>{needsApproval ? <Link className="btn primary" to={approvalPath}>Submit for approval<Shortcut keys="mod+enter" /></Link> : <button type="button" className="btn primary" disabled={commitDisabled} onClick={() => void commit()}>Commit merge<Shortcut keys="mod+enter" /></button>}</div></div>
      <ErrorNote error={error} />
      <div className="stack">
        <section className="card" aria-labelledby="merge-records"><div className="card-header"><h2 id="merge-records">Records to compare</h2><span className="meta">serial, asset tag or unit id · identifiers stay as aliases</span></div><div className="card-body"><div className="correction-source-fields"><div className="field"><label htmlFor="merge-first">Record A · serial or unit</label><input id="merge-first" className="input serial" value={firstId} onChange={(event) => { setFirstId(event.target.value); setRecords(null); setPreview(null); }} placeholder="Scan or type a serial" /></div><div className="field"><label htmlFor="merge-second">Record B · serial or unit</label><input id="merge-second" className="input serial" value={secondId} onChange={(event) => { setSecondId(event.target.value); setRecords(null); setPreview(null); }} placeholder="Scan or type a serial" /></div></div><button type="button" className="btn" disabled={!firstId.trim() || !secondId.trim() || firstId.trim() === secondId.trim() || busy} onClick={() => void load()}>Load both records</button></div></section>
        {records && <><div className="correction-records"><RecordCard label="A" unit={records.first} survivor={survivorId === records.first.unitId} onKeep={() => { setSurvivorId(records.first.unitId); setPreview(null); }} /><RecordCard label="B" unit={records.second} survivor={survivorId === records.second.unitId} onKeep={() => { setSurvivorId(records.second.unitId); setPreview(null); }} /></div>
          <section className="card" aria-labelledby="merge-identifiers"><div className="card-header"><h2 id="merge-identifiers">Identifiers after the merge <span className="meta">both sets kept on the survivor</span></h2></div><DataGrid dense columns={columns} page={{ rows: identifiers, total: identifiers.length, page: 1, pageSize: 25, continuation: null }} rowId={(row) => row.identifierId} onPage={() => undefined} onRowClick={openIdentifier} activeRowId={active} emptyText="Neither record has an identifier." viewportHeight={300} footer={`${identifiers.length.toLocaleString()} identifiers · complete list`} gridId="merge-unit-identifiers" userId={session.user.id} defaultSort={{ key: "value", dir: "asc" }} defaultPageSize={25} /></section>
          <div className="cols-3-2" style={{ alignItems: "start" }}><section className="card" aria-labelledby="merge-preview"><div className="card-header"><h2 id="merge-preview">Preview <span className="meta">computed by the command</span></h2></div>{preview ? <><div className="preview-summary"><Figure label="Units" value={preview.preview.unitCount} /><Figure label="Will change" value={preview.preview.change} tone="ok" /><Figure label="Blocked" value={preview.preview.block} tone="bad" /><Figure label="Events" value={preview.preview.unitEvents.length} /></div><table className="table dense"><thead><tr><th>Re-pointed or recorded</th><th>Detail</th><th className="num">Count</th></tr></thead><tbody><PreviewRow label="Unit events" detail="recorded on the survivor and absorbed alias" count={preview.preview.unitEvents.length} /><PreviewRow label="Entitlement changes" detail="superseded or re-pointed by the command" count={preview.preview.entitlementChanges.length} /><PreviewRow label="Certificate versions" detail="new immutable versions" count={preview.preview.certificateVersions.length} /><PreviewRow label="Print jobs" detail="labels requested by the command" count={preview.preview.printJobs.length} /></tbody></table>{preview.preview.refusal && <div className="note bad"><strong>{humanise(preview.preview.refusal.rule)}.</strong>&nbsp;{preview.preview.refusal.message}</div>}</> : <div className="empty"><h2>Preview before deciding</h2><p className="hint">The merge command compares both records, recommends a survivor and computes every affected record.</p></div>}</section>
            <section className="card" aria-labelledby="merge-reason"><div className="card-header"><h2 id="merge-reason">Reason and approval</h2></div><div className="card-body"><div className="form-grid"><div className="field span"><label htmlFor="merge-reason-code">Reason code</label><select id="merge-reason-code" className="select" value={reason} onChange={(event) => { setReason(event.target.value); setPreview(null); }}><option value="vendor_serial_keyed_twice">vendor_serial_keyed_twice</option><option value="duplicate_record">duplicate_record</option><option value="migration_duplicate">migration_duplicate</option></select></div><div className="field span"><label htmlFor="merge-note">Note</label><textarea id="merge-note" className="textarea" value={note} onChange={(event) => { setNote(event.target.value); setPreview(null); }} placeholder="Why these records represent one physical unit" /></div>
              {approval && <div className={`span note ${approval.status === "allowed" ? "ok" : approval.status === "needs_approval" ? "warn" : "bad"}`}><strong>{approval.status === "allowed" ? "May commit." : approval.status === "needs_approval" ? "Approval required." : "Merge refused."}</strong>&nbsp;{approval.approverRoles.length ? `Needs ${approval.approverRoles.map(humanise).join(" + ")}.` : `Commit roles: ${approval.commitRoles.map(humanise).join(", ") || "none"}.`}</div>}
              <div className="span note"><strong>Would be refused</strong>&nbsp;if the records describe two physical units; choose the collision acknowledgement alternative instead when the duplicate vendor value is genuine.</div>{committed && <div className="span note ok"><strong>Merge committed.</strong>&nbsp;{committed.rowsRepointed.toLocaleString()} rows re-pointed · correction {shortId(committed.correctionId, 12)}.</div>}</div></div></section></div></>}
      </div>
    </main>
    <Rail label="Merge units"><Section title="Invariant"><p className="muted">No record is deleted. The absorbed unit remains readable as a merged alias, and every existing external reference continues to resolve to the survivor.</p></Section>{preview && <Section title="Approval"><Kv rows={[["Status", humanise(preview.approval.status)], ["Commit roles", preview.approval.commitRoles.map(humanise).join(", ")], ["Approver roles", preview.approval.approverRoles.map(humanise).join(", ") || "none"], ["Recommended", shortId(preview.recommendedSurvivorUnitId, 12)]]} /></Section>}</Rail>
  </>;
}

function RecordCard({ label, unit, survivor, onKeep }: { label: "A" | "B"; unit: UnitDetail; survivor: boolean; onKeep: () => void }) {
  const internal = unit.identifiers.find((row) => row.type === "internal_serial")?.rawValue ?? unit.identifiers[0]?.rawValue ?? shortId(unit.unitId, 12);
  const vendor = unit.identifiers.find((row) => row.type === "vendor_serial")?.rawValue ?? "none";
  const opened = unit.episodes.find((row) => !row.validTo);
  const referenceNames = useReferenceNames({ items: [unit.itemId], parties: [unit.ownerPartyRef, opened?.partyRef] });
  return <section className={`card correction-record${survivor ? " survivor" : ""}`} aria-labelledby={`merge-record-${label}`}><div className="card-header"><h2 id={`merge-record-${label}`}>Record {label} · {survivor ? "survivor" : "absorbed"}</h2><label className="radiorow"><input type="radio" name="merge-survivor" checked={survivor} onChange={onKeep} /><span>{survivor ? "Keep" : "Keep instead"}</span></label></div><div className="card-body"><Kv rows={[["Internal serial", <span className="serial">{internal}</span>], ["Vendor serial", <span className="serial">{vendor}</span>], ["Item", referenceNames.item(unit.itemId)], ["Origin", `${humanise(unit.origin)} · ${fmtDate(unit.lastEventTime)}`], ["Owner", unit.ownerPartyRef ? referenceNames.party(unit.ownerPartyRef) : "none"], ["Custody", opened ? `${humanise(opened.kind)} · ${opened.partyRef ? referenceNames.party(opened.partyRef) : humanise(opened.locationKind)}` : "none"], ["Lifecycle", statusLabel(unit.lifecycleStatus)], ["Events", unit.eventCount.toLocaleString()]]} /></div></section>;
}
function Figure({ label, value, tone }: { label: string; value: number; tone?: "ok" | "bad" }) { return <span className="figure"><span className="k">{label}</span><span className={`v${tone ? ` ${tone}-text` : ""}`}>{value.toLocaleString()}</span></span>; }
function PreviewRow({ label, detail, count }: { label: string; detail: string; count: number }) { return <tr><td className="primary">{label}</td><td>{detail}</td><td className="num">{count.toLocaleString()}</td></tr>; }

/** A unit id as typed, or the unit a serial, superseded serial or asset tag resolves to through the workbench resolver. */
async function unitIdFor(entry: string): Promise<string> {
  const value = entry.trim();
  if (GUID.test(value)) return value;
  const result = await workbenchApi.resolve({ kind: "identifier", value });
  const hit = result.candidates[0];
  if (!hit) throw new Error(`Nothing resolves from "${value}".`);
  if (result.candidates.length > 1) throw new Error(`"${value}" matches ${result.candidates.length} units; use the unit id.`);
  return hit.resolvedUnitId;
}
