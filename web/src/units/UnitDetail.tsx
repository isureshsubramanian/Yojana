import { useCallback, useEffect, useMemo, useState } from "react";
import { Link, useParams, useSearchParams } from "react-router";
import { units, DEFAULT_LEGAL_ENTITY, newCommandId, now, type HistoryRow, type Page, type UnitDetail } from "../api/units";
import { StatusBadge, TimelineRail, type TimelineEntry } from "../components";
import { openDrawer, updateDrawer } from "../shell/drawerStore";
import { Rail } from "../shell/Drawer";
import { Icon } from "../shell/Icons";
import { Breadcrumbs } from "../shell/PageHeader";
import { ErrorNote, Kv, Section, UnitLink, useActor } from "./common";
import { documentLabel, fmtDate, fmtDateTime, humanise, shortId, statusLabel, statusTone } from "./format";
import { EntitlementPanel, openAmendEntitlement, openAttachEntitlement, type EntitlementSummary } from "../entitlements/EntitlementPanel";
import type { UnitEntitlementDetail } from "../entitlements/types";

const HISTORY_PAGE = 20;
const FLAGS = ["lost", "stolen", "written_off", "clone_seen", "recalled", "authenticity_failed"];
const REASONS = ["typo", "misread", "relabel", "regulatory"];

/** Unit detail (P1 screen 2): the print-ready record. History on the left, identifiers, episodes and as-built on the right; the state strip is always visible. */
export function Component() {
  const { unitId = "" } = useParams();
  const [params] = useSearchParams();
  const resolvedFrom = params.get("resolved");
  const [detail, setDetail] = useState<UnitDetail | null>(null);
  const [history, setHistory] = useState<Page<HistoryRow> | null>(null);
  const [skip, setSkip] = useState(0);
  const [error, setError] = useState<unknown>(null);
  const [version, setVersion] = useState(0);
  const [selectedEvent, setSelectedEvent] = useState<number | null>(null);
  const [tab, setTab] = useState<"history" | "entitlements" | "identifiers" | "custody" | "asbuilt">("entitlements");
  const [entitlementSummary, setEntitlementSummary] = useState<EntitlementSummary | null>(null);
  const [entitlementDetail, setEntitlementDetail] = useState<UnitEntitlementDetail | null>(null);
  const [reloadEntitlements, setReloadEntitlements] = useState<() => void>(() => () => undefined);

  useEffect(() => {
    const controller = new AbortController();
    setError(null);
    Promise.all([units.detail(unitId, controller.signal), units.history(unitId, skip, HISTORY_PAGE, controller.signal)])
      .then(([d, h]) => { setDetail(d); setHistory(h); })
      .catch((e) => { if (!(e instanceof DOMException && e.name === "AbortError")) setError(e); });
    return () => controller.abort();
  }, [unitId, skip, version]);

  const reload = useCallback(() => setVersion((v) => v + 1), []);
  const primary = detail?.identifiers.find((i) => i.validTo === null && i.type === "internal_serial") ?? detail?.identifiers.find((i) => i.validTo === null);
  const title = primary?.rawValue ?? (detail ? `unit ${shortId(detail.unitId)}` : "…");
  const owner = detail?.episodes.find((e) => e.kind === "ownership" && e.validTo === null);
  const custody = detail?.episodes.find((e) => e.kind === "custody" && e.validTo === null);
  const flags = useMemo(() => { try { return JSON.parse(detail?.overlayFlags ?? "[]") as string[]; } catch { return []; } }, [detail?.overlayFlags]);
  const openIdentifiers = detail?.identifiers.filter((i) => i.validTo === null) ?? [];
  const supersededCount = (detail?.identifiers.length ?? 0) - openIdentifiers.length;

  /** Timeline: events newest first, ownership episodes as headings when the owner changes along the way. */
  const entries = useMemo<TimelineEntry[]>(() => {
    if (!history || !detail) return [];
    const out: TimelineEntry[] = [];
    let lastEpisode: string | null = null;
    for (const e of history.items) {
      const t = e.eventTime;
      const ep = detail.episodes.find((x) => x.kind === "ownership" && x.validFrom <= t && (x.validTo === null || x.validTo > t));
      const label = ep ? `Ownership · ${humanise(ep.partyType)} ${shortId(ep.partyRef)} · from ${fmtDate(ep.validFrom)}${ep.validTo ? ` to ${fmtDate(ep.validTo)}` : ""}` : "No owner recorded";
      if (label !== lastEpisode) { out.push({ kind: "episode", label }); lastEpisode = label; }
      const key = e.eventType === "unit.released" || e.eventType === "ownership.opened" || e.eventType === "ownership.transferred" || e.eventType === "unit.commissioned";
      const bad = e.eventType === "ownership.closed" || e.eventType === "unit.scrapped" || e.eventType === "unit.flag_set";
      out.push({
        kind: "event", title: eventTitle(e.eventType), code: e.eventType,
        meta: [fmtDateTime(e.eventTime), e.bizStep ? humanise(e.bizStep) : null, e.disposition ? humanise(e.disposition) : null, e.document ? documentLabel(e.document) : null, e.actorRef].filter(Boolean).join(" · "),
        recordedAt: e.recordedTime !== e.eventTime ? fmtDateTime(e.recordedTime) : null,
        emphasis: key ? "key" : bad ? "bad" : null,
      });
    }
    return out;
  }, [history, detail]);

  const eventAt = (index: number): HistoryRow | undefined => {
    // entries interleave episode headings; map the clicked entry back to its event
    let n = -1;
    for (let i = 0; i <= index; i++) if (entries[i]?.kind === "event") n++;
    return history?.items[n];
  };

  const showEvent = (index: number) => {
    const e = eventAt(index);
    if (!e) return;
    setSelectedEvent(index);
    let attributes = e.attributes;
    try { attributes = JSON.stringify(JSON.parse(e.attributes ?? "{}"), null, 2); } catch { /* raw text */ }
    openDrawer({ eyebrow: "Selected · history event", title: eventTitle(e.eventType) }, (
      <>
        <h3>Event</h3>
        <Kv rows={[["Type", e.eventType], ["Business time", fmtDateTime(e.eventTime)], ["Recorded time", fmtDateTime(e.recordedTime)], ["Seq", e.seq.toLocaleString()], ["Actor", e.actorRef], ["Biz step", humanise(e.bizStep) || "—"], ["Disposition", humanise(e.disposition) || "—"], ["Document", e.document ? documentLabel(e.document) : "—"], ["Event id", shortId(e.eventId, 12)]]} />
        <h3 style={{ marginTop: 16 }}>Attributes</h3>
        <pre className="raw" style={{ whiteSpace: "pre-wrap", overflow: "auto", maxHeight: 360 }}>{attributes ?? "{}"}</pre>
      </>
    ));
  };

  const correct = () => detail && openDrawer({ eyebrow: "Command · CorrectIdentifier", title }, <CorrectForm unitId={unitId} types={openIdentifiers.map((i) => i.type)} onDone={reload} />);
  const flag = () => detail && openDrawer({ eyebrow: "Command · SetUnitFlag", title }, <FlagForm unitId={unitId} flags={flags} onDone={reload} />);

  useEffect(() => { if (window.location.hash === "#correct" && detail) correct(); }, [detail]); // eslint-disable-line react-hooks/exhaustive-deps

  return (
    <>
      <main className="content" id="main" tabIndex={-1}>
        <Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Units", to: "/units" }, { label: title }]} />
        <div className="page-header">
          <div>
            <div className="eyebrow">Unit · item {detail ? shortId(detail.itemId) : "…"}{detail?.modelVersionId ? ` · model ${shortId(detail.modelVersionId)}` : ""}{detail?.lotId ? ` · lot ${shortId(detail.lotId)}` : ""}{detail?.expiryDate ? ` · expires ${detail.expiryDate}` : ""}</div>
            <h1 className="mono" style={{ fontFamily: "var(--font-mono)", letterSpacing: 0 }}>{title}</h1>
            <p className="sub">
              {detail ? tab === "entitlements" && entitlementSummary ? <>Covered to {fmtDate(entitlementSummary.coveredTo)} · {entitlementSummary.live} live entitlements in {entitlementSummary.families} coverage families · owned by {owner ? `${humanise(owner.partyType)} ${shortId(owner.partyRef)}` : "no recorded owner"} · {entitlementSummary.hasOpenClaims ? "open claims" : "no open claims"}</> : <>{statusLabel(detail.lifecycleStatus)} · custody {humanise(detail.locationKind) || "unknown"}{custody?.partyRef ? `, ${humanise(custody.partyType)} ${shortId(custody.partyRef)}` : ""}{owner ? ` · owned by ${humanise(owner.partyType)} ${shortId(owner.partyRef)} since ${fmtDate(owner.validFrom)}` : " · no owner recorded"} · {detail.asBuilt.filter((a) => a.removedAt === null).length} components as built</> : "Loading…"}
              {resolvedFrom && <> · <span className="serial">{resolvedFrom}</span> is a superseded identifier of this unit</>}
            </p>
          </div>
          <div className="actions">
            <button type="button" className="btn" onClick={() => window.print()}><Icon name="print" />Print</button>
            {tab === "entitlements" ? <><button type="button" className="btn" onClick={() => openAttachEntitlement(unitId, entitlementDetail, reloadEntitlements)}>Attach extended or add-on…</button><button type="button" className="btn primary" onClick={() => openAmendEntitlement(entitlementDetail, reloadEntitlements)}><Icon name="corr" />Amend entitlement…</button></> : <><button type="button" className="btn" onClick={flag}>Overlay flag…</button><button type="button" className="btn primary" onClick={correct}><Icon name="corr" />Correct identifier…</button></>}
          </div>
        </div>
        <ErrorNote error={error} />

        {detail && (
          <div className="card statebar" aria-label="Current state" style={{ marginBottom: "var(--gap)" }}>
            <span><span className="k">Lifecycle</span><span className="v">{detail.lifecycleStatus}</span></span>
            <span><span className="k">Disposition</span><span className="v">{detail.disposition}</span></span>
            <span><span className="k">Custody</span><span className="v">{detail.locationKind ?? "—"}{detail.locationId ? ` · ${shortId(detail.locationId)}` : ""}</span></span>
            <span><span className="k">Owner</span><span className="v">{detail.ownerPartyRef ? shortId(detail.ownerPartyRef) : "—"}</span></span>
            <span><span className="k">Condition</span><span className="v">{detail.conditionGrade ?? "—"}</span></span>
            <span><span className="k">Overlay flags</span><span className={`v${flags.length ? " bad-text" : " muted"}`}>{flags.length ? flags.join(", ") : "none"}</span></span>
            <span><span className="k">Last event seq</span><span className="v">{detail.lastEventSeq.toLocaleString()} · {fmtDateTime(detail.lastEventTime)}</span></span>
          </div>
        )}

        <nav className="tabs unit-tabs" aria-label="Unit record sections">
          <button type="button" aria-current={tab === "history" ? "page" : undefined} onClick={() => setTab("history")}>History {detail?.eventCount ?? "…"}</button>
          <button type="button" aria-current={tab === "entitlements" ? "page" : undefined} onClick={() => setTab("entitlements")}>Entitlements {entitlementSummary ? `${entitlementSummary.live} live · ${entitlementSummary.closed} closed` : "…"}</button>
          <button type="button" aria-current={tab === "identifiers" ? "page" : undefined} onClick={() => setTab("identifiers")}>Identifiers {detail?.identifiers.length ?? "…"}</button>
          <button type="button" aria-current={tab === "custody" ? "page" : undefined} onClick={() => setTab("custody")}>Custody and ownership {detail?.episodes.length ?? "…"}</button>
          <button type="button" aria-current={tab === "asbuilt" ? "page" : undefined} onClick={() => setTab("asbuilt")}>As-built {detail ? `${detail.asBuilt.filter((row) => !row.removedAt).length} of ${detail.asBuilt.length}` : "…"}</button>
        </nav>

        {tab === "entitlements" && <EntitlementPanel unitId={unitId} onSummary={(summary, entitlementRows, reloadRows) => { setEntitlementSummary(summary); setEntitlementDetail(entitlementRows); setReloadEntitlements(() => reloadRows); }} />}

        {tab !== "entitlements" && <div className={`cols-2 unit-tab-content tab-${tab}`}>
          <section className="card unit-history" aria-labelledby="h-tl">
            <div className="card-header"><h2 id="h-tl">History <span className="meta">{detail?.eventCount.toLocaleString() ?? "…"} events</span></h2><span className="meta">Business time · recorded time on hover · click for the attributes</span></div>
            <div className="card-body" style={{ paddingTop: 4 }}>
              {entries.length === 0 ? <p className="hint">No events on this page.</p> : <TimelineRail entries={entries} onSelect={showEvent} selectedIndex={selectedEvent} />}
            </div>
            <div className="card-footer">
              <span>Events {history ? `${history.skip + 1}–${Math.min(history.skip + history.items.length, history.total)}` : "…"} of <strong>{history?.total.toLocaleString() ?? "…"}</strong> · ordered by business time, recorded time, seq</span>
              <span className="pager"><button type="button" className="btn" disabled={skip === 0} onClick={() => { setSkip(Math.max(0, skip - HISTORY_PAGE)); setSelectedEvent(null); }}>Newer</button><button type="button" className="btn" disabled={!history || history.skip + history.items.length >= history.total} onClick={() => { setSkip(skip + HISTORY_PAGE); setSelectedEvent(null); }}>Older</button></span>
            </div>
          </section>

          <div className="stack unit-secondary">
            <section className="card unit-identifiers" aria-labelledby="h-id">
              <div className="card-header"><h2 id="h-id">Identifiers <span className="meta">{openIdentifiers.length} open{supersededCount ? ` · ${supersededCount} superseded` : ""}</span></h2><button type="button" className="btn ghost sm" onClick={correct}>Correct…</button></div>
              <table className="table dense">
                <colgroup><col style={{ width: "34%" }} /><col style={{ width: "44%" }} /><col style={{ width: "22%" }} /></colgroup>
                <thead><tr><th>Type · scheme</th><th>Value</th><th>From</th></tr></thead>
                <tbody>
                  {detail?.identifiers.map((i) => (
                    <tr key={i.identifierId}>
                      <td className={`primary${i.validTo ? " muted" : ""}`}>{i.type}<span className="sub">{i.validTo ? `superseded${i.correctionReason ? ` · ${i.correctionReason}` : ""}` : `${i.scheme} · scope ${i.scopeKey === "TENANT" ? "tenant" : "item"}${i.issuerPartyRef ? ` · issuer ${shortId(i.issuerPartyRef)}` : ""}`}</span></td>
                      <td className={`serial${i.validTo ? " muted" : ""}`} title={i.rawValue}>{i.validTo ? <s>{i.rawValue}</s> : i.rawValue}{i.validTo && i.supersededByIdentifierId && <> → {detail.identifiers.find((x) => x.identifierId === i.supersededByIdentifierId)?.rawValue ?? "…"}</>}</td>
                      <td className="muted">{fmtDate(i.validFrom)}<span className="sub">{i.validTo ? `to ${fmtDate(i.validTo)}` : "open"}</span></td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </section>

            <section className="card unit-custody" aria-labelledby="h-ep">
              <div className="card-header"><h2 id="h-ep">Custody and ownership <span className="meta">episodes · effective-dated</span></h2><span className="meta">1 open of each kind</span></div>
              <table className="table dense">
                <colgroup><col style={{ width: "22%" }} /><col style={{ width: "38%" }} /><col style={{ width: "20%" }} /><col style={{ width: "20%" }} /></colgroup>
                <thead><tr><th>Kind</th><th>Party · location</th><th>From</th><th>To</th></tr></thead>
                <tbody>
                  {detail?.episodes.map((e) => (
                    <tr key={e.episodeId}>
                      <td><StatusBadge tone={e.validTo ? "neutral" : "ok"}>{e.kind === "ownership" ? "Ownership" : "Custody"}</StatusBadge></td>
                      <td className="primary">{e.kind === "ownership" ? `${humanise(e.partyType)} ${shortId(e.partyRef)}` : `${humanise(e.locationKind)}${e.locationId ? ` · ${shortId(e.locationId)}` : ""}${e.partyRef ? ` · ${humanise(e.partyType)} ${shortId(e.partyRef)}` : ""}`}<span className="sub">{[e.reason ? `reason ${humanise(e.reason)}` : null, e.source ? `source ${humanise(e.source)}` : null, e.confidence ? `confidence ${e.confidence}` : null, e.customerPoRef ? `PO ${e.customerPoRef}` : null, e.dueBack ? `due back ${e.dueBack}` : null].filter(Boolean).join(" · ")}</span></td>
                      <td>{fmtDate(e.validFrom)}<span className="sub">{e.openingDocument ? documentLabel(e.openingDocument) : ""}</span></td>
                      <td className={e.validTo ? undefined : "muted"}>{e.validTo ? fmtDate(e.validTo) : "open"}<span className="sub">{e.closingDocument ? documentLabel(e.closingDocument) : ""}</span></td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </section>

            <section className="card unit-asbuilt" aria-labelledby="h-ab">
              <div className="card-header">
                <h2 id="h-ab">As-built <span className="meta">{detail?.buildRecords.length ? `frozen at release ${fmtDate(detail.buildRecords[0]?.releasedAt)} · BuildRecord v${detail.buildRecords[0]?.version}` : "no release record yet"}</span></h2>
                {detail && <StatusBadge tone={detail.asBuilt.some((a) => a.removedAt === null) ? "ok" : "neutral"}>{detail.asBuilt.filter((a) => a.removedAt === null).length} installed</StatusBadge>}
              </div>
              <div className="card-body" style={{ paddingTop: 8, paddingBottom: 8 }}>
                <ul className="tree">
                  <li><span className="node"><span className="item">item {detail ? shortId(detail.itemId) : "…"}</span><span className="serial">{title}</span><span className="hint">· this unit</span></span><StatusBadge tone="neutral">Parent</StatusBadge></li>
                  {detail?.asBuilt.map((a) => (
                    <li className="l1" key={a.linkId}>
                      <span className="node"><span className="slot">{a.slot ?? "—"}</span><span className="item">{a.childItemId ? shortId(a.childItemId) : "lot"}</span>{a.childUnitId ? <UnitLink unitId={a.childUnitId}>{a.childSerial ?? shortId(a.childUnitId)}</UnitLink> : <span className="serial muted">{a.childLotId ? `lot ${shortId(a.childLotId)}` : ""}{a.quantity != null ? ` · qty ${a.quantity}` : ""}</span>}<span className="hint">· {a.relation}{a.throughPhantomItemId ? ` · through phantom ${shortId(a.throughPhantomItemId)}` : ""}{a.releaseStatus ? ` · ${a.releaseStatus}` : ""}{a.removedAt ? ` · removed ${fmtDate(a.removedAt)}` : ""}</span></span>
                      <StatusBadge tone={a.removedAt ? "neutral" : a.childUnitId ? "ok" : "neutral"}>{a.removedAt ? "Removed" : a.childUnitId ? "Captured" : "Lot"}</StatusBadge>
                    </li>
                  ))}
                  {detail && detail.asBuilt.length === 0 && <li><span className="node"><span className="hint">No components recorded.</span></span><span /></li>}
                </ul>
              </div>
              <div className="card-footer"><span>{detail?.buildRecords.length ? `${detail.buildRecords.length} build record${detail.buildRecords.length === 1 ? "" : "s"} · latest hash ${detail.buildRecords[0]?.payloadHash.slice(0, 12)}…` : "As-maintained equals as-built until a release freezes it"}</span><Link to={`/units/completeness?serials=${encodeURIComponent(title)}`}>Completeness query</Link></div>
            </section>
          </div>
        </div>}
      </main>
      <Rail label="Lineage">
        <Section title="Lineage">
          <Kv rows={[["Derived from", detail?.derivedFromUnitId ? <UnitLink unitId={detail.derivedFromUnitId}>{shortId(detail.derivedFromUnitId)}</UnitLink> : "—"], ["Merged into", detail?.mergedIntoUnitId ? <UnitLink unitId={detail.mergedIntoUnitId}>{shortId(detail.mergedIntoUnitId)}</UnitLink> : "—"], ["Parent", detail?.parentUnitId ? <UnitLink unitId={detail.parentUnitId}>{shortId(detail.parentUnitId)}</UnitLink> : "—"], ["Children", detail ? `${detail.asBuilt.filter((a) => a.childUnitId && !a.removedAt).length} serialised · ${detail.asBuilt.filter((a) => !a.childUnitId && !a.removedAt).length} lot` : "—"], ["Origin", humanise(detail?.origin)], ["Serialisation point", humanise(detail?.serialisationPoint)], ["Retention class", detail?.retentionClass ?? "—"], ["History depth", detail?.historyDepth ?? "—"]]} />
        </Section>
        <Section title="Last events">
          {history?.items.slice(0, 3).map((e) => <div className="job" key={e.eventId}><div><div className="t">{e.eventType}</div><div className="s">{fmtDateTime(e.eventTime)} · recorded {fmtDateTime(e.recordedTime).slice(11)}</div></div><StatusBadge tone="neutral">seq …{String(e.seq).slice(-4)}</StatusBadge></div>)}
        </Section>
        <Section title="Later phases add here">
          <p className="muted">Workbench entry (P3), RMA and refurbishment (P5), and certificates (P6). Entitlements are now part of the unit record above.</p>
        </Section>
      </Rail>
    </>
  );
}

function eventTitle(code: string): string {
  const tail = code.split(".").pop() ?? code;
  const h = tail.replace(/_/g, " ");
  return h[0]!.toUpperCase() + h.slice(1);
}

function CorrectForm({ unitId, types, onDone }: { unitId: string; types: string[]; onDone: () => void }) {
  const actor = useActor();
  const [type, setType] = useState(types[0] ?? "internal_serial");
  const [value, setValue] = useState("");
  const [reason, setReason] = useState(REASONS[0]!);
  const [approver, setApprover] = useState("");
  const [note, setNote] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const submit = async () => {
    setBusy(true); setError(null);
    try {
      const r = await units.correctIdentifier({ commandId: newCommandId(), unitId, identifierType: type, newRawValue: value, reason, eventTime: now(), actorRef: actor, legalEntityId: DEFAULT_LEGAL_ENTITY, approverRef: approver || null, note: note || null });
      updateDrawer({ eyebrow: "Recorded · unit.identifier_corrected", title: r.newNormalisedValue, mono: true }, <><h3>Done</h3><Kv rows={[["Before", r.oldNormalisedValue], ["After", r.newNormalisedValue], ["Old row", "closed, still searchable"]]} /></>);
      onDone();
    } catch (e) { setError(e); } finally { setBusy(false); }
  };
  return (
    <>
      <h3>Correct identifier</h3>
      <div className="form-grid">
        <div className="field"><label htmlFor="c-type">Identifier type</label><select id="c-type" className="select" value={type} onChange={(e) => setType(e.target.value)}>{types.map((t) => <option key={t} value={t}>{t}</option>)}</select></div>
        <div className="field"><label htmlFor="c-value">New value</label><input id="c-value" className="input serial" value={value} onChange={(e) => setValue(e.target.value)} autoComplete="off" spellCheck={false} /></div>
        <div className="field"><label htmlFor="c-reason">Reason</label><select id="c-reason" className="select" value={reason} onChange={(e) => setReason(e.target.value)}>{REASONS.map((r) => <option key={r} value={r}>{r}</option>)}</select></div>
        <div className="field"><label htmlFor="c-approver">Approver <span className="hint">· required after the unit appeared on an external document</span></label><input id="c-approver" className="input" value={approver} onChange={(e) => setApprover(e.target.value)} /></div>
        <div className="field"><label htmlFor="c-note">Note</label><textarea id="c-note" className="textarea" value={note} onChange={(e) => setNote(e.target.value)} /></div>
        <ErrorNote error={error} />
        <div className="act"><button type="button" className="btn primary block" disabled={!value || busy} aria-busy={busy} onClick={submit}>Record correction</button></div>
      </div>
      <p className="hint" style={{ marginTop: 12 }}>A new row replaces the open one of that type; the old value stays searchable and resolves to this unit. No stock moves; the events stay as they are.</p>
    </>
  );
}

function FlagForm({ unitId, flags, onDone }: { unitId: string; flags: string[]; onDone: () => void }) {
  const actor = useActor();
  const [flag, setFlag] = useState(FLAGS[0]!);
  const [reason, setReason] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const isSet = flags.includes(flag);
  const submit = async () => {
    setBusy(true); setError(null);
    try {
      const command = { commandId: newCommandId(), unitId, flag, eventTime: now(), actorRef: actor, legalEntityId: DEFAULT_LEGAL_ENTITY, reason: reason || null };
      const r = isSet ? await units.unflag(command) : await units.flag(command);
      updateDrawer({ eyebrow: `Recorded · ${isSet ? "unit.flag_cleared" : "unit.flag_set"}`, title: flag }, <><h3>Done</h3><Kv rows={[["Flags now", r.flags.length ? r.flags.join(", ") : "none"], ["Investigation", r.investigationRaised ? "raised · the unit reappeared after a custody fact" : "none"]]} /></>);
      onDone();
    } catch (e) { setError(e); } finally { setBusy(false); }
  };
  return (
    <>
      <h3>Overlay flag</h3>
      <div className="form-grid">
        <div className="field"><label htmlFor="f-flag">Flag</label><select id="f-flag" className="select" value={flag} onChange={(e) => setFlag(e.target.value)}>{FLAGS.map((f) => <option key={f} value={f}>{f}{flags.includes(f) ? " · set" : ""}</option>)}</select></div>
        <div className="field"><label htmlFor="f-reason">Reason</label><input id="f-reason" className="input" value={reason} onChange={(e) => setReason(e.target.value)} /></div>
        <ErrorNote error={error} />
        <div className="act"><button type="button" className={`btn block${isSet ? "" : " primary"}`} disabled={busy} aria-busy={busy} onClick={submit}>{isSet ? `Clear ${flag}` : `Set ${flag}`}</button></div>
      </div>
      <p className="hint" style={{ marginTop: 12 }}>Flags overlay any status and block entitlement evaluation. Clearing lost or stolen after a custody fact raises an investigation; nothing is deleted.</p>
    </>
  );
}
