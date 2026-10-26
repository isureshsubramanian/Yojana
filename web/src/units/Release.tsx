import { useEffect, useMemo, useState } from "react";
import { Link } from "react-router";
import { selections, RELEASE_OPERATION, now, type BulkExecutionResult, type BulkOperationArguments, type BulkPreviewResult, type ResolveSelectionSetResult, type SelectionSource, type UnitOutcome } from "../api/units";
import { LegalEntityPicker, useLegalEntities } from "../api/legalEntities";
import { SelectionPreview, Shortcut, StatusBadge, useGridState, useModEnter, type PreviewOutcome, type PreviewResult } from "../components";
import { useSession } from "../session/session";
import { openDrawer } from "../shell/drawerStore";
import { Rail } from "../shell/Drawer";
import { useHandoff, useLegacyParam } from "../shell/handoff";
import { Breadcrumbs } from "../shell/PageHeader";
import { usePeople } from "../people/people";
import { ErrorNote, Kv, Section, useActor } from "./common";
import { humanise, sentence, shortId, splitSerials } from "./format";

const KINDS = [["document", "Document"], ["customer", "Customer"], ["item", "Item"], ["date_range", "Date range"], ["pasted_list", "Pasted list"], ["serial_range", "Serial range"]] as const;
const REASONS = ["final_test_passed", "rework_complete", "qa_waiver"];
const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const PAGE = 50;

function toPreview(r: BulkPreviewResult): PreviewResult {
  return {
    rows: r.page.map((o) => ({ id: o.unitId, serial: o.serial ?? shortId(o.unitId), current: o.detail, next: o.outcome === "change" ? "released" : null, outcome: (o.outcome === "change" ? "change" : o.outcome === "skip" ? "skip" : "block") as PreviewOutcome, reason: o.reason })),
    total: r.total, page: Math.floor(r.pageSkip / Math.max(1, r.pageTake)) + 1, pageSize: r.pageTake || PAGE, continuation: null,
    summary: { resolved: r.total, change: r.change, skip: r.skip, block: r.block }, matches: r.total,
  };
}

/** Selection-set preview (P1 screen 6): address units by document, customer, item, dates, list or range; the command computes every outcome; execute is one transaction. */
export function Component() {
  const session = useSession();
  const actor = useActor();
  const { value: legalEntities } = useLegalEntities();
  const [legalEntityId, setLegalEntityId] = useState("");
  useEffect(() => { if (legalEntities?.defaultId) setLegalEntityId((current) => current || legalEntities.defaultId!); }, [legalEntities?.defaultId]);
  const person = usePeople([actor]);
  useLegacyParam("serials", "serials");
  const handed = useHandoff().serials ?? "";
  const [kind, setKind] = useState<string>(handed ? "pasted_list" : "serial_range");
  const [docType, setDocType] = useState("production_order");
  const [docId, setDocId] = useState("");
  const [partyRef, setPartyRef] = useState("");
  const [itemId, setItemId] = useState("");
  const [from, setFrom] = useState("");
  const [to, setTo] = useState("");
  const [eventType, setEventType] = useState("");
  const [list, setList] = useState(handed);
  useEffect(() => { if (handed) { setKind("pasted_list"); setList(handed); } }, [handed]);
  const [scheme, setScheme] = useState("default");
  const [rangeStart, setRangeStart] = useState("");
  const [rangeEnd, setRangeEnd] = useState("");
  const [sourceDocType, setSourceDocType] = useState("production_order");
  const [sourceDocId, setSourceDocId] = useState("");
  const [reason, setReason] = useState(REASONS[0]!);
  const [note, setNote] = useState("");
  const [signature, setSignature] = useState("");
  const [approver, setApprover] = useState("");
  const [resolved, setResolved] = useState<ResolveSelectionSetResult | null>(null);
  const [preview, setPreview] = useState<BulkPreviewResult | null>(null);
  const [find, setFind] = useState("");
  const [outcome, setOutcome] = useState<PreviewOutcome | null>(null);
  const grid = useGridState("release-selection-preview", session.user.id, { key: "priority", dir: "asc" }, PAGE);
  const [loading, setLoading] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [executed, setExecuted] = useState<BulkExecutionResult | null>(null);

  const source = useMemo<SelectionSource>(() => ({
    kind,
    document: kind === "document" && GUID.test(docId) ? { type: docType, id: docId } : null,
    partyRef: kind === "customer" && GUID.test(partyRef) ? partyRef : null,
    itemId: kind === "item" && GUID.test(itemId) ? itemId : null,
    from: kind === "date_range" && from ? new Date(from).toISOString() : null,
    to: kind === "date_range" && to ? new Date(to).toISOString() : null,
    eventType: kind === "date_range" && eventType ? eventType : null,
    serials: kind === "pasted_list" ? splitSerials(list) : null,
    scheme: kind === "serial_range" ? scheme : null,
    rangeStart: kind === "serial_range" ? rangeStart : null,
    rangeEnd: kind === "serial_range" ? rangeEnd : null,
  }), [kind, docType, docId, partyRef, itemId, from, to, eventType, list, scheme, rangeStart, rangeEnd]);

  const args = useMemo<BulkOperationArguments | null>(() => GUID.test(sourceDocId)
    ? { eventTime: now(), actorRef: actor, ...(legalEntities && legalEntities.entities.length > 1 ? { legalEntityId } : {}), sourceDocument: { type: sourceDocType, id: sourceDocId }, reason, note: note || null, extra: signature ? { release_signature_ref: signature } : null }
    : null, [sourceDocType, sourceDocId, reason, note, signature, actor, legalEntities, legalEntityId]);

  const resolve = async () => {
    setBusy(true); setError(null); setExecuted(null); setPreview(null);
    try { setResolved(await selections.resolve(source, actor)); grid.reset(); } catch (e) { setError(e); } finally { setBusy(false); }
  };

  useEffect(() => {
    if (!resolved || !args) { return; }
    const controller = new AbortController();
    setLoading(true);
    selections.preview(resolved.selectionId, RELEASE_OPERATION, args, (grid.page - 1) * grid.pageSize, grid.pageSize, find || null, outcome, grid.sort.key, grid.sort.dir, controller.signal)
      .then((p) => { setPreview(p); setLoading(false); })
      .catch((e) => { if (!(e instanceof DOMException && e.name === "AbortError")) { setError(e); setLoading(false); } });
    return () => controller.abort();
  }, [resolved, args, grid.page, grid.pageSize, grid.sort, find, outcome]);

  const execute = async () => {
    if (!resolved || !args) return;
    setBusy(true); setError(null);
    try { setExecuted(await selections.execute(resolved.selectionId, RELEASE_OPERATION, args, approver || null)); grid.reset(); setFind(""); setOutcome(null); } catch (e) { setError(e); } finally { setBusy(false); }
  };
  useModEnter(execute, !preview || !args || preview.change === 0 || busy || !!executed);

  const rowClick = (o: UnitOutcome) => openDrawer({ eyebrow: `Selected unit · ${o.outcome}`, title: o.serial ?? shortId(o.unitId), mono: true }, (
    <>
      <h3>Outcome</h3>
      <Kv rows={[["Outcome", <span className={o.outcome === "block" ? "bad-text" : o.outcome === "skip" ? "warn-text" : "ok-text"}>{humanise(o.outcome)}</span>], ["Rule", o.reason ? <span className="serial">{o.reason}</span> : "—"], ["Detail", o.detail ?? "—"], ["Operation", RELEASE_OPERATION]]} />
      <div className="act"><Link className="btn primary block" to="/units/completeness" state={o.serial ? { serials: o.serial } : null}>Open completeness</Link><Link className="btn block" to={`/units/${o.unitId}`}>Open unit</Link></div>
    </>
  ));

  const previewResult = preview ? toPreview(preview) : null;
  const sourceReady = (kind === "document" && GUID.test(docId)) || (kind === "customer" && GUID.test(partyRef)) || (kind === "item" && GUID.test(itemId)) || (kind === "date_range" && from && to) || (kind === "pasted_list" && splitSerials(list).length > 0) || (kind === "serial_range" && rangeStart && rangeEnd);

  return (
    <>
      <main className="content" id="main" tabIndex={-1}>
        <Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Units", to: "/units" }, { label: "Release units" }]} />
        <div className="page-header">
          <div>
            <div className="eyebrow">Bulk change · release units · preview, then execute</div>
            <h1>Release units</h1>
            <p className="sub">{resolved ? <>Selection set {shortId(resolved.selectionId)} · {resolved.resolved} units{resolved.unknown.length ? ` · ${resolved.unknown.length} unknown` : ""}{resolved.rejected.length ? ` · ${resolved.rejected.length} rejected` : ""} · one transaction, one source document, one approval</> : "Address the units, set the source document, preview every outcome, then execute as one transaction."}</p>
          </div>
          <div className="actions">
            <Link className="btn ghost" to="/units">Discard</Link>
            <button type="button" className="btn" disabled={!sourceReady || busy} aria-busy={busy} onClick={resolve}>Resolve selection</button>
            <button type="button" className="btn primary" disabled={!preview || !args || preview.change === 0 || busy || !!executed} aria-busy={busy} onClick={execute}>Execute · {preview?.change ?? 0} units<Shortcut keys="mod+enter" /></button>
          </div>
        </div>

        <div className="cols-2-3">
          <section className="card" aria-labelledby="h-set">
            <div className="card-header"><h2 id="h-set">Selection set</h2><span className="meta">Step 1 of 2</span></div>
            <div className="card-body">
              <div className="form-grid">
                {legalEntities && legalEntities.entities.length > 1 && <LegalEntityPicker id="release-legal-entity" label="Legal entity" value={legalEntityId} entities={legalEntities.entities} onChange={setLegalEntityId} hint="the company releasing these units" />}
                <div className="field span">
                  <span className="lbl">Address units by</span>
                  <div className="segmented plain" role="group" aria-label="Selection source" style={{ flexWrap: "wrap" }}>
                    {KINDS.map(([id, label]) => <button type="button" key={id} aria-pressed={kind === id} onClick={() => setKind(id)}>{label}</button>)}
                  </div>
                </div>
                {kind === "document" && <><div className="field"><label htmlFor="s-doctype">Document type</label><input id="s-doctype" className="input" value={docType} onChange={(e) => setDocType(e.target.value)} /></div><div className="field"><label htmlFor="s-docid">Document id</label><input id="s-docid" className="input serial" value={docId} onChange={(e) => setDocId(e.target.value)} placeholder="guid" /></div></>}
                {kind === "customer" && <div className="field span"><label htmlFor="s-party">Customer party ref</label><input id="s-party" className="input serial" value={partyRef} onChange={(e) => setPartyRef(e.target.value)} placeholder="guid" /></div>}
                {kind === "item" && <div className="field span"><label htmlFor="s-item">Item id</label><input id="s-item" className="input serial" value={itemId} onChange={(e) => setItemId(e.target.value)} placeholder="guid" /></div>}
                {kind === "date_range" && <><div className="field"><label htmlFor="s-from">From</label><input id="s-from" className="input" type="date" value={from} onChange={(e) => setFrom(e.target.value)} /></div><div className="field"><label htmlFor="s-to">To</label><input id="s-to" className="input" type="date" value={to} onChange={(e) => setTo(e.target.value)} /></div><div className="field span"><label htmlFor="s-event">Event type <span className="hint">· optional, for example unit.commissioned</span></label><input id="s-event" className="input" value={eventType} onChange={(e) => setEventType(e.target.value)} /></div></>}
                {kind === "pasted_list" && <div className="field span"><label htmlFor="s-list">Serials <span className="hint">· {splitSerials(list).length} distinct</span></label><textarea id="s-list" className="textarea serial" value={list} onChange={(e) => setList(e.target.value)} /></div>}
                {kind === "serial_range" && <><div className="field"><label htmlFor="s-scheme">Scheme</label><input id="s-scheme" className="input" value={scheme} onChange={(e) => setScheme(e.target.value)} /></div><div className="field" /><div className="field"><label htmlFor="s-start">From</label><input id="s-start" className="input serial" value={rangeStart} onChange={(e) => setRangeStart(e.target.value)} /></div><div className="field"><label htmlFor="s-end">To</label><input id="s-end" className="input serial" value={rangeEnd} onChange={(e) => setRangeEnd(e.target.value)} /></div></>}
                {resolved && <div className={`span note ${resolved.rejected.length ? "bad" : "ok"}`}><strong>{resolved.resolved} units resolved.</strong>&nbsp;{resolved.unknown.length ? `${resolved.unknown.length} unknown: ${resolved.unknown.slice(0, 8).join(", ")}${resolved.unknown.length > 8 ? " …" : ""}. ` : ""}{resolved.rejected.length ? `Rejected: ${resolved.rejected.join("; ")}` : "A range expands within one block of its scheme, never by string arithmetic."}</div>}
                <div className="field"><label htmlFor="s-srcdoctype">Source document type</label><input id="s-srcdoctype" className="input" value={sourceDocType} onChange={(e) => setSourceDocType(e.target.value)} /></div>
                <div className="field"><label htmlFor="s-srcdocid">Source document id</label><input id="s-srcdocid" className="input serial" value={sourceDocId} onChange={(e) => setSourceDocId(e.target.value)} placeholder="guid · required for the preview" /></div>
                <div className="field"><label htmlFor="s-reason">Reason</label><select id="s-reason" className="select" value={reason} onChange={(e) => setReason(e.target.value)}>{REASONS.map((r) => <option key={r} value={r}>{sentence(r)}</option>)}</select></div>
                <div className="field"><label htmlFor="s-sig">Release signature ref</label><input id="s-sig" className="input" value={signature} onChange={(e) => setSignature(e.target.value)} placeholder={`sig:${actor}`} /></div>
                <div className="field"><label htmlFor="s-approver">Approver <span className="hint">· recorded with the execution</span></label><input id="s-approver" className="input" value={approver} onChange={(e) => setApprover(e.target.value)} /></div>
                <div className="field span"><label htmlFor="s-note">Note</label><textarea id="s-note" className="textarea" style={{ minHeight: 64 }} value={note} onChange={(e) => setNote(e.target.value)} /></div>
                {executed && <div className="span note ok"><strong>Executed{executed.replayed ? " (replay)" : ""}.</strong>&nbsp;{executed.done} released, {executed.skipped} skipped, {executed.blocked} blocked · validation report {shortId(executed.validationReportId)}.</div>}
                <ErrorNote error={error} />
              </div>
            </div>
          </section>

          <div onClick={(e) => {
            const target = e.target as HTMLElement;
            if (target.closest("a, input, button, select")) return;
            const tr = target.closest("tr");
            const serial = tr?.querySelector("td")?.textContent?.trim();
            const row = preview?.page.find((o) => (o.serial ?? shortId(o.unitId)) === serial);
            if (row) rowClick(row);
          }}>
            <SelectionPreview preview={previewResult} loading={loading} currentLabel="Current" nextLabel="Next" changeLabel="Release" query={find} outcome={outcome}
              onQuery={(q) => { setFind(q); grid.reset(); }} onOutcome={(o) => { setOutcome(o); grid.reset(); }} onPage={grid.onPage}
              grid={grid} gridId="release-selection-preview" userId={session.user.id} />
          </div>
        </div>
      </main>
      <Rail label="Execution">
        <Section title="Routing">
          <Kv rows={[["Initiator", person(actor)], ["Approver", approver || "not required"], ["Source document", sourceDocId ? `${sourceDocType} ${shortId(sourceDocId)}` : "—"], ["Transaction", "1 · all or nothing"], ["Selection", resolved ? shortId(resolved.selectionId) : "—"]]} />
        </Section>
        <Section title="What will be recorded">
          <div className="job"><div><div className="t">unit.released</div><div className="s">one event per unit · seq assigned in order</div></div><StatusBadge tone="neutral">{preview?.change ?? 0}</StatusBadge></div>
          <div className="job"><div><div className="t">BuildRecord v1</div><div className="s">as-built frozen at release</div></div><StatusBadge tone="neutral">{preview?.change ?? 0}</StatusBadge></div>
          <div className="job"><div><div className="t">Unit.Released</div><div className="s">published through the outbox</div></div><StatusBadge tone="neutral">{preview?.change ?? 0}</StatusBadge></div>
          <div className="job"><div><div className="t">Validation report</div><div className="s">{(preview?.skip ?? 0) + (preview?.block ?? 0)} units with reasons · kept with the command</div></div><StatusBadge tone="neutral">1</StatusBadge></div>
        </Section>
        <Section title="Rules applied">
          <p className="muted">Execution is one transaction with one source document and one approval; partial failure after commit is impossible. Blocked and skipped units keep their state and are listed with the reason. The same preview panel serves every bulk command in later phases.</p>
        </Section>
      </Rail>
    </>
  );
}
