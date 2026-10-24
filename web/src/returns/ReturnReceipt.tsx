import { useCallback, useEffect, useMemo, useState } from "react";
import { Link, useSearchParams } from "react-router";
import { ScanInput, Shortcut, StatusBadge } from "../components";
import { isUrlScanFormat } from "../components/scan";
import { useTenantTime } from "../components/tenantTime";
import { units } from "../api/units";
import { LocationField, useReferenceNames } from "../corrections/ReferenceFields";
import { Rail } from "../shell/Drawer";
import { Icon } from "../shell/Icons";
import { Breadcrumbs } from "../shell/PageHeader";
import { ErrorNote, Kv, Section } from "../units/common";
import { returnsApi } from "./api";
import { CONDITION_GRADES, gradeOptionState, humanise, receiptTone, receiptValuationRule, valuationRuleForGrade } from "./format";
import type { RmaDetail, RmaDetailLine, RmaListRow } from "./types";
import "./returns.css";

export function Component() {
  const tenantTime = useTenantTime();
  const [params] = useSearchParams();
  const [detail, setDetail] = useState<RmaDetail | null>(null);
  const [activeLineId, setActiveLineId] = useState<string | null>(null);
  const [scan, setScan] = useState("");
  const [received, setReceived] = useState<Record<string, string | null>>({});
  const [resolvedMatches, setResolvedMatches] = useState<Set<string>>(new Set());
  const [unreadable, setUnreadable] = useState<Set<string>>(new Set());
  const [grades, setGrades] = useState<Record<string, string>>({});
  const [notes, setNotes] = useState<Record<string, string>>({});
  const [locationId, setLocationId] = useState<string | null>(null);
  const [receivedAt, setReceivedAt] = useState(() => tenantTime.localInput(new Date().toISOString()));
  const [today, setToday] = useState<RmaListRow[]>([]);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<string | null>(null);
  const [error, setError] = useState<unknown>(null);
  const applyDetail = useCallback((next: RmaDetail) => {
    setDetail(next); setActiveLineId(next.lines.find((line) => !line.receiptOutcome)?.rmaLineId ?? next.lines[0]?.rmaLineId ?? null);
    setGrades(Object.fromEntries(next.lines.map((line) => [line.rmaLineId, line.conditionGrade ?? "defective"])));
  }, []);
  const loadToday = useCallback((signal?: AbortSignal) => returnsApi.list({ stage: "received", minimumAgeDays: null, maximumAgeDays: null, exceptionsOnly: false, q: "" }, null, 50, signal)
    .then((page) => { const day = tenantTime.localInput(new Date().toISOString()).slice(0, 10); setToday(page.rows.filter((row) => tenantTime.localInput(row.receivedAt).slice(0, 10) === day)); }), [tenantTime]);
  useEffect(() => { setReceivedAt(tenantTime.localInput(new Date().toISOString())); }, [tenantTime]);
  useEffect(() => {
    const controller = new AbortController(); const id = params.get("rmaId");
    if (id) void returnsApi.detail(id, controller.signal).then(applyDetail).catch((reason) => { if (!(reason instanceof DOMException && reason.name === "AbortError")) setError(reason); });
    void loadToday(controller.signal).catch(() => undefined); return () => controller.abort();
  }, [applyDetail, loadToday, params]);
  useEffect(() => {
    const onKey = (event: KeyboardEvent) => { if (event.key === "F9") { event.preventDefault(); void receiveReturn(); } };
    window.addEventListener("keydown", onKey); return () => window.removeEventListener("keydown", onKey);
  });
  const resolveScan = async (value: string) => {
    setBusy(true); setError(null); setMessage(null);
    try {
      const parsed = await units.scan(value);
      if (isUrlScanFormat(parsed.format)) { setMessage("This is a web address, not a serial"); return; }
      let current = detail;
      if (!current) {
        const matches = await returnsApi.list({ stage: "", minimumAgeDays: null, maximumAgeDays: null, exceptionsOnly: false, q: value }, null, 10);
        const candidate = matches.rows.find((row) => ["authorised", "in_transit"].includes(row.stage)) ?? matches.rows[0];
        if (!candidate) throw new Error("No return matches this RMA or unit label.");
        current = await returnsApi.detail(candidate.rmaId); applyDetail(current);
        if (candidate.rmaNumber?.toLowerCase() === value.toLowerCase()) { setScan(""); setMessage(`${candidate.rmaNumber} loaded · scan each unit now`); return; }
      }
      if (!["authorised", "in_transit"].includes(current.stage)) throw new Error(`This return is ${humanise(current.stage).toLowerCase()} and cannot be received at the bench.`);
      const lookup = await units.lookup(value);
      const unitIds = new Set(lookup.hits.filter((hit) => hit.open).map((hit) => hit.unitId));
      const matched = current.lines.find((line) => unitIds.has(line.authorisedUnitId ?? line.requestedUnitId));
      const target = matched ?? current.lines.find((line) => !(line.rmaLineId in received) && !unreadable.has(line.rmaLineId));
      if (!target) throw new Error("Every line on this return already has a scanned unit.");
      setReceived((existing) => ({ ...existing, [target.rmaLineId]: value }));
      setResolvedMatches((existing) => { const next = new Set(existing); if (matched) next.add(target.rmaLineId); else next.delete(target.rmaLineId); return next; });
      setUnreadable((existing) => { const next = new Set(existing); next.delete(target.rmaLineId); return next; });
      setActiveLineId(target.rmaLineId); setScan("");
      setMessage(matched ? `Matched ${value} to line ${target.sequence}` : `${value} is not the authorised unit · line ${target.sequence} will be held as a mismatch`);
    } catch (reason) { setError(reason); } finally { setBusy(false); }
  };
  const activeLine = detail?.lines.find((line) => line.rmaLineId === activeLineId) ?? detail?.lines[0] ?? null;
  const activeValue = activeLine ? received[activeLine.rmaLineId] : undefined;
  const canReceive = !!detail && ["authorised", "in_transit"].includes(detail.stage) && !!locationId
    && !!receivedAt && detail.lines.every((line) => typeof received[line.rmaLineId] === "string" || unreadable.has(line.rmaLineId));
  const receiveReturn = async () => {
    if (!detail || !locationId || !canReceive) return;
    setBusy(true); setError(null);
    try {
      await returnsApi.receive(detail.rmaId, {
        receivingLocationId: locationId, dispatchedAt: null, receivedAt: tenantTime.fromLocalInput(receivedAt),
        lines: detail.lines.map((line) => ({ rmaLineId: line.rmaLineId, receivedIdentifier: unreadable.has(line.rmaLineId) ? null : { value: received[line.rmaLineId]!, issuerPartyRef: null, identifierType: null, erpSerialId: null }, conditionGrade: grades[line.rmaLineId] ?? "defective", valuationRule: valuationRuleForGrade(grades[line.rmaLineId] ?? "defective"), outcome: "accepted", exceptionNote: notes[line.rmaLineId]?.trim() || null })),
      });
      const next = await returnsApi.detail(detail.rmaId); applyDetail(next); setMessage(`${next.rmaNumber ?? "Return"} received into quarantine`); await loadToday();
    } catch (reason) { setError(reason); } finally { setBusy(false); }
  };
  const markUnreadable = () => {
    if (!activeLine) { setMessage("Load an RMA before recording an unreadable label."); return; }
    setUnreadable((existing) => new Set(existing).add(activeLine.rmaLineId));
    setResolvedMatches((existing) => { const next = new Set(existing); next.delete(activeLine.rmaLineId); return next; });
    setReceived((existing) => ({ ...existing, [activeLine.rmaLineId]: null })); setScan(""); setMessage(`Line ${activeLine.sequence} will use a provisional identity and stay in quarantine.`);
  };
  const itemIds = detail?.lines.flatMap((line) => [line.requestedItemId, line.receivedItemId]) ?? [];
  const partyIds = detail ? [detail.returningPartyRef] : [];
  const names = useReferenceNames({ items: itemIds, parties: partyIds });
  const todayNames = useReferenceNames({ items: today.map((row) => row.primaryItemId) });
  const matched = activeLine && activeValue ? resolvedMatches.has(activeLine.rmaLineId)
    || activeValue.toLowerCase() === (activeLine.authorisedSerial ?? activeLine.requestedSerial ?? "").toLowerCase() : false;
  const activeGrade = activeLine ? grades[activeLine.rmaLineId] ?? "defective" : "defective";
  const valuation = activeLine
    ? receiptValuationRule(activeLine, activeGrade)
    : { rule: valuationRuleForGrade(activeGrade), proposed: true };
  return <>
    <main className="content" id="main" tabIndex={-1}>
      <Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Returns", to: "/returns" }, { label: "Receipt bench" }]} />
      <div className="page-header"><div><div className="eyebrow">Returns dock · quarantine receipt</div><h1>Receipt bench</h1><p className="sub">Scan the RMA or the unit · everything received goes to quarantine first · the receipt binds to the unit actually in your hands</p></div><div className="actions"><button type="button" className="btn" onClick={markUnreadable}>Unit label unreadable</button><button type="button" className="btn primary" disabled={!canReceive || busy} title={!canReceive ? "Load the RMA, scan every line, and enter a configured quarantine location" : undefined} onClick={() => void receiveReturn()}><Icon name="check" />Receive into quarantine<kbd className="key">F9</kbd></button></div></div>
      <section className="card" style={{ marginBottom: "var(--gap)" }}><div className="card-body"><div className="toolbar"><ScanInput value={scan} onChange={setScan} onSubmit={(submission) => void resolveScan(submission.value)} ariaLabel="Scan RMA number, serial or asset tag" placeholder="RMA, serial or asset tag" hint={<Shortcut keys="/" />} size="lg" primary autoFocus disabled={busy} /><span className="grow" /><span className="hint" aria-live="polite">{message ?? (detail ? `${detail.rmaNumber ?? "Return"} loaded · ${detail.lines.filter((line) => line.rmaLineId in received || unreadable.has(line.rmaLineId)).length} of ${detail.lines.length} units captured` : "Scan the RMA number or the first unit")}</span></div></div></section>
      <div className="cols-3-2 returns-receipt-layout">
        <div className="stack">
          <section className="card" aria-labelledby="receipt-match-heading"><div className="card-header"><h2 id="receipt-match-heading">{detail ? `${detail.rmaNumber ?? "Return"} · ${activeLine ? `line ${activeLine.sequence} of ${detail.lines.length}` : "choose a line"}` : "Waiting for a return"}</h2>{activeLine && <StatusBadge tone={unreadable.has(activeLine.rmaLineId) ? "warn" : activeValue ? matched ? "ok" : "bad" : "neutral"}>{unreadable.has(activeLine.rmaLineId) ? "Provisional" : activeValue ? matched ? "Matched" : "Mismatch" : "Not scanned"}</StatusBadge>}</div><div className="card-body">
            {detail && detail.lines.length > 1 && <div className="claims-segmented" style={{ marginBottom: 14 }}>{detail.lines.map((line) => <button type="button" className={`chip${line.rmaLineId === activeLine?.rmaLineId ? " on" : ""}`} key={line.rmaLineId} onClick={() => setActiveLineId(line.rmaLineId)}>Line {line.sequence}{received[line.rmaLineId] !== undefined || unreadable.has(line.rmaLineId) ? " · captured" : ""}</button>)}</div>}
            {activeLine ? <><div className="returns-pair"><div className="returns-pair-side"><div className="returns-pair-label">Authorised</div><div className="serial">{activeLine.authorisedSerial ?? activeLine.requestedSerial ?? "No serial recorded"}</div><div className="sub muted">{names.item(activeLine.requestedItemId)}{activeLine.description ? ` · ${activeLine.description}` : ""}</div></div><div className="returns-pair-arrow" aria-hidden="true">{matched ? "=" : "→"}</div><div className={`returns-pair-side${activeValue && !matched ? " bad" : ""}`}><div className="returns-pair-label">Scanned</div><div className="serial">{unreadable.has(activeLine.rmaLineId) ? "Provisional identity" : activeValue ?? "Scan this unit"}</div><div className="sub muted">{unreadable.has(activeLine.rmaLineId) ? "label unreadable · nothing guessed" : activeValue ? matched ? "identifier resolves to the authorised unit" : "held as mismatch for review" : "waiting for a unit label"}</div></div></div><div className="kv returns-wide-kv" style={{ marginTop: 14 }}><span className="k">Customer</span><span className="v">{names.party(detail?.returningPartyRef)}</span><span className="k">Coverage</span><span className="v">{humanise(activeLine.coverageSource)} · {activeLine.coveragePct ?? 0}%</span><span className="k">Original sale</span><span className="v">Resolved on the RMA</span><span className="k">Parent</span><span className="v">{activeLine.parentUnitId ? "Child-unit return · parent will be marked incomplete" : "Standalone unit"}</span></div></> : <p className="muted">Scan an RMA number or a unit identifier to resolve its return line.</p>}
          </div></section>
          {activeLine && <section className="card" aria-labelledby="receipt-grade-heading"><div className="card-header"><h2 id="receipt-grade-heading">Grade on receipt</h2><span className="meta">sets the valuation rule · inspection can change it later</span></div><div className="card-body"><div className="returns-options" role="radiogroup" aria-label="Condition grade">{CONDITION_GRADES.map((grade) => { const state = gradeOptionState(grade); const selected = (grades[activeLine.rmaLineId] ?? "defective") === grade; return <label className={`returns-option${selected ? " on" : ""}${state.enabled ? "" : " off"}`} key={grade} title={state.reason ?? undefined}><input type="radio" name="condition-grade" disabled={!state.enabled || !!activeLine.receiptOutcome} checked={selected} onChange={() => setGrades((current) => ({ ...current, [activeLine.rmaLineId]: grade }))} /><span>{humanise(grade)}<span className="s">{state.reason ?? gradeHelp(grade)}</span></span></label>; })}</div><div className="returns-inline-fields"><LocationField id="receipt-location" label="Quarantine location" value={locationId} onChange={setLocationId} kind="quarantine" hint="Required · custody moves here at the receipt business time" /><label className="field"><span className="lbl">Received at</span><input className="input" type="datetime-local" value={receivedAt} onChange={(event) => setReceivedAt(event.target.value)} /></label><label className="field"><span className="lbl">Valuation rule{valuation.proposed ? " · proposed" : ""}</span><span className="returns-readonly-value">{humanise(valuation.rule)}</span></label></div><label className="field" style={{ marginTop: 14 }}><span className="lbl">Inspection note</span><textarea className="textarea" value={notes[activeLine.rmaLineId] ?? ""} onChange={(event) => setNotes((current) => ({ ...current, [activeLine.rmaLineId]: event.target.value }))} placeholder="Fault, packaging and visible-condition observations" /></label></div></section>}
          <ErrorNote error={error} />
        </div>
        <div className="stack">
          <section className="card"><div className="card-header"><h2>Will be recorded</h2><span className="meta">one transaction</span></div><table className="table dense"><tbody><tr><td className="primary">Return received</td><td>authorised or in transit → received</td></tr><tr><td className="primary">Custody moved</td><td>current custody → quarantine</td></tr><tr><td className="primary">Condition graded{valuation.proposed ? " · proposed" : ""}</td><td>{humanise(activeGrade)} · {humanise(valuation.rule)}</td></tr><tr><td className="primary">Receipt outcome</td><td>{activeValue ? matched ? "accepted" : "mismatch" : unreadable.size ? "provisional identity" : "waiting for scan"}</td></tr></tbody></table><div className="card-footer"><span>Business time {receivedAt ? tenantTime.dateTime(tenantTime.fromLocalInput(receivedAt)) : "—"}</span></div></section>
          <section className="card"><div className="card-header"><h2>When the label cannot be read</h2></div><div className="card-body"><p className="muted">The unit is received under a provisional identity and stays in quarantine until it is matched. Nothing is guessed.</p></div></section>
          <section className="card"><div className="card-header"><h2>Received today</h2><span className="meta">{today.length} returns</span></div><table className="table dense"><tbody>{today.length ? today.map((row) => <tr key={row.rmaId}><td>{tenantTime.time(row.receivedAt)}</td><td><Link className="serial" to={`/returns/${row.rmaId}`}>{row.primarySerial ?? row.rmaNumber ?? "Return"}</Link><span className="sub">{todayNames.item(row.primaryItemId)}</span></td><td><StatusBadge tone={receiptTone(row.receiptOutcome)}>{humanise(row.receiptOutcome)}</StatusBadge></td></tr>) : <tr><td className="muted" colSpan={3}>Nothing received today.</td></tr>}</tbody></table></section>
        </div>
      </div>
    </main>
    <Rail label="Receipt"><Section title="Next"><p className="muted">Inspection completes on the RMA, where the disposition is chosen.</p>{detail && <div className="act"><Link className="btn block" to={`/returns/${detail.rmaId}`}>Open return</Link></div>}</Section></Rail>
  </>;
}

function gradeHelp(grade: string): string { if (grade === "new") return "sealed · never powered"; if (grade === "open_box") return "unused · packaging opened"; if (grade === "used") return "working · signs of use"; if (grade === "defective") return "fault confirmed or reported"; if (grade === "scrap") return "physically damaged beyond repair"; return "controlled condition grade"; }
