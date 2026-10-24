import { WhatsCoveredPanel } from "../entitlements/WhatsCoveredPanel";
import { coverItemsApi, unitTab, type WhatsCoveredPage } from "../entitlements/coverItems";
import { entitlementApi } from "../entitlements/api";
import { coverApi } from "../entitlements/coverApi";
import { UnitTabBar } from "./UnitTabBar";
import { UnitCustomerMail } from "../mail/UnitCustomerMail";
import { useCallback, useEffect, useMemo, useState } from "react";
import { Link, useParams, useSearchParams } from "react-router";
import { units, newCommandId, now, type HistoryRow, type Page, type UnitDetail } from "../api/units";
import { StatusBadge, TimelineRail, type TimelineEntry } from "../components";
import { openDrawer, updateDrawer } from "../shell/drawerStore";
import { Rail } from "../shell/Drawer";
import { Icon } from "../shell/Icons";
import { useHandoff, useLegacyParam } from "../shell/handoff";
import { Breadcrumbs } from "../shell/PageHeader";
import { usePeople } from "../people/people";
import { ErrorNote, Kv, Section, UnitLink } from "./common";
import { fmtDate, fmtDateTime, humanise, sentence, statusLabel } from "./format";
import { EntitlementPanel, openAmendEntitlement, openAttachEntitlement, type EntitlementSummary } from "../entitlements/EntitlementPanel";
import { CoverSinceSalePanel } from "../entitlements/CoverSinceSalePanel";
import type { UnitEntitlementDetail } from "../entitlements/types";
import { useReferenceNames } from "../corrections/ReferenceFields";
import { QuestionsPanel } from "../questions/QuestionsPanel";

const HISTORY_PAGE = 20;
const FLAGS = ["lost", "stolen", "written_off", "clone_seen", "recalled", "authenticity_failed"];
const REASONS = ["typo", "misread", "relabel", "regulatory"];

/** Unit detail (P1 screen 2): the print-ready record. History on the left, identifiers, episodes and as-built on the right; the state strip is always visible. */
export function Component() {
  const { unitId = "" } = useParams();
  const [params] = useSearchParams();
  useLegacyParam("resolved", "resolvedFrom");
  const resolvedFrom = useHandoff().resolvedFrom ?? null;
  const [detail, setDetail] = useState<UnitDetail | null>(null);
  const [history, setHistory] = useState<Page<HistoryRow> | null>(null);
  const [skip, setSkip] = useState(0);
  const [error, setError] = useState<unknown>(null);
  const [version, setVersion] = useState(0);
  const [selectedEvent, setSelectedEvent] = useState<number | null>(null);
  const tab = unitTab(params.get("tab"));
  const [whatsCovered, setWhatsCovered] = useState<WhatsCoveredPage | null>(null);
  const [changeCount, setChangeCount] = useState<number | undefined>();
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

  useEffect(() => {
    const c = new AbortController();
    Promise.all([entitlementApi.detailForUnit(unitId, c.signal), coverItemsApi.sections(unitId, "", 0, 7, 0, 7, c.signal), coverApi.unit(unitId, c.signal)])
      .then(([entitlements, covered, changes]) => {
        setEntitlementDetail(entitlements); setWhatsCovered(covered); setChangeCount(changes.changeCount);
        const live = entitlements.entitlements.filter((e) => e.storedStatus !== "closed");
        setEntitlementSummary({ live: live.length, closed: entitlements.entitlements.length - live.length, families: new Set(live.map((e) => e.coverageFamily)).size,
          coveredTo: live.map((e) => e.endDate).filter((d): d is string => !!d).sort().at(-1) ?? null, owner: null, hasOpenClaims: entitlements.hasOpenClaims });
      }).catch((e) => { if (!c.signal.aborted) setError(e); });
    return () => c.abort();
  }, [unitId, version]);

  const reload = useCallback(() => setVersion((v) => v + 1), []);
  const person = usePeople(history?.items.map((e) => e.actorRef) ?? []);
  const referenceNames = useReferenceNames({
    parties: [detail?.ownerPartyRef, ...(detail?.identifiers.map((i) => i.issuerPartyRef) ?? []), ...(detail?.episodes.map((e) => e.partyRef) ?? [])],
    locations: [detail?.locationId, ...(detail?.episodes.map((e) => e.locationId) ?? [])],
    items: [detail?.itemId, ...(detail?.asBuilt.flatMap((row) => [row.childItemId, row.throughPhantomItemId]) ?? [])],
    documents: [lotDocument(detail?.lotId), ...(history?.items.map((e) => e.document) ?? []), ...(detail?.episodes.flatMap((e) => [e.openingDocument, e.closingDocument]) ?? []), ...(detail?.asBuilt.map((row) => lotDocument(row.childLotId)) ?? [])],
  });
  const primary = detail?.identifiers.find((i) => i.validTo === null && i.type === "internal_serial") ?? detail?.identifiers.find((i) => i.validTo === null);
  const title = primary?.rawValue ?? (detail ? "Serial not recorded" : "…");
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
      const label = ep ? `Ownership · ${referenceNames.party(ep.partyRef)} · from ${fmtDate(ep.validFrom)}${ep.validTo ? ` to ${fmtDate(ep.validTo)}` : ""}` : "No owner recorded";
      if (label !== lastEpisode) { out.push({ kind: "episode", label }); lastEpisode = label; }
      const key = e.eventType === "unit.released" || e.eventType === "ownership.opened" || e.eventType === "ownership.transferred" || e.eventType === "unit.commissioned";
      const bad = e.eventType === "ownership.closed" || e.eventType === "unit.scrapped" || e.eventType === "unit.flag_set";
      out.push({
        kind: "event", title: eventTitle(e.eventType), code: e.eventType,
        meta: [fmtDateTime(e.eventTime), e.bizStep ? humanise(e.bizStep) : null, e.disposition ? humanise(e.disposition) : null, e.document ? referenceNames.document(e.document) : null, person(e.actorRef)].filter(Boolean).join(" · "),
        recordedAt: e.recordedTime !== e.eventTime ? fmtDateTime(e.recordedTime) : null,
        emphasis: key ? "key" : bad ? "bad" : null,
      });
    }
    return out;
  }, [history, detail, person, referenceNames]);

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
        <Kv rows={[["Type", e.eventType], ["Business time", fmtDateTime(e.eventTime)], ["Recorded time", fmtDateTime(e.recordedTime)], ["Seq", e.seq.toLocaleString()], ["Actor", person(e.actorRef)], ["Biz step", humanise(e.bizStep) || "—"], ["Disposition", humanise(e.disposition) || "—"], ["Document", e.document ? referenceNames.document(e.document) : "—"]]} />
        <h3 style={{ marginTop: 16 }}>Attributes</h3>
        <pre className="raw" style={{ whiteSpace: "pre-wrap", overflow: "auto", maxHeight: 360 }}>{attributes ?? "{}"}</pre>
      </>
    ));
  };

  const correct = () => detail && openDrawer({ eyebrow: "Unit · correct an identifier", title }, <CorrectForm unitId={unitId} types={openIdentifiers.map((i) => i.type)} onDone={reload} />);
  const flag = () => detail && openDrawer({ eyebrow: "Unit · set or clear a flag", title }, <FlagForm unitId={unitId} flags={flags} onDone={reload} />);

  useEffect(() => { if (window.location.hash === "#correct" && detail) correct(); }, [detail]); // eslint-disable-line react-hooks/exhaustive-deps

  return (
    <>
      <main className="content" id="main" tabIndex={-1}>
        <Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Units", to: "/units" }, { label: title }]} />
        <div className="page-header">
          <div>
            <div className="eyebrow">Unit · item {detail ? referenceNames.item(detail.itemId) : "…"}{detail?.lotId ? <> · lot {referenceNames.lot(lotDocument(detail.lotId))}</> : ""}{detail?.expiryDate ? ` · expires ${detail.expiryDate}` : ""}</div>
            <h1 className="mono" style={{ fontFamily: "var(--font-mono)", letterSpacing: 0 }}>{title}</h1>
            <p className="sub">
              {detail ? tab === "whats-covered" ? <>What this unit is covered for today, from all its live covers together. Each line names the cover that grants or excludes it. Customer wording is what you can read out or print.</> : tab === "entitlements" && entitlementSummary ? <>Covered to {fmtDate(entitlementSummary.coveredTo)} · {entitlementSummary.live} live entitlements in {entitlementSummary.families} coverage families · owned by {owner ? referenceNames.party(owner.partyRef) : "no recorded owner"} · {entitlementSummary.hasOpenClaims ? "open claims" : "no open claims"}</> : <>{statusLabel(detail.lifecycleStatus)} · custody {humanise(detail.locationKind) || "unknown"}{custody?.partyRef ? `, ${referenceNames.party(custody.partyRef)}` : ""}{owner ? ` · owned by ${referenceNames.party(owner.partyRef)} since ${fmtDate(owner.validFrom)}` : " · no owner recorded"} · {detail.asBuilt.filter((a) => a.removedAt === null).length} components as built</> : "Loading…"}
              {resolvedFrom && <> · <span className="serial">{resolvedFrom}</span> is a superseded identifier of this unit</>}
            </p>
          </div>
          <div className="actions">
            <button type="button" className="btn" onClick={() => { if (tab === "whats-covered") window.open(`/api/units/${unitId}/whats-covered/sheet.pdf`, "_blank", "noopener"); else window.print(); }}><Icon name="print" />Print</button>
            {tab !== "whats-covered" && <Link className="btn" to={`/corrections?unit=${unitId}`} state={primary?.rawValue ? { entry: primary.rawValue } : null}>Something is wrong…</Link>}
            {tab === "whats-covered" ? <>{whatsCovered?.customerEmailEnabled && <Link className="btn" to={`/units/${unitId}/email`}>E-mail to customer…</Link>}<Link className="btn primary" to={`/claims/new?unit=${unitId}`}>Start a claim</Link></> : tab === "entitlements" ? <><button type="button" className="btn" onClick={() => openAttachEntitlement(unitId, entitlementDetail, reloadEntitlements)}>Attach extended or add-on…</button><button type="button" className="btn primary" onClick={() => openAmendEntitlement(entitlementDetail, reloadEntitlements)}><Icon name="corr" />Amend entitlement…</button></> : <><button type="button" className="btn" onClick={flag}>Overlay flag…</button><button type="button" className="btn primary" onClick={correct}><Icon name="corr" />Correct identifier…</button></>}
          </div>
        </div>
        <ErrorNote error={error} />
        {detail?.ownerAndHolderDiffer && <div className="note warn" role="status"><strong>Owner and holder differ.</strong>&nbsp;This unit is held at the customer site recorded for {detail.custodyPartyRef ? referenceNames.party(detail.custodyPartyRef) : "another party"}, while {detail.ownerPartyRef ? referenceNames.party(detail.ownerPartyRef) : "another party"} owns it.</div>}

        {detail && (
          <div className="card statebar" aria-label="Current state" style={{ marginBottom: "var(--gap)" }}>
            <span><span className="k">Lifecycle</span><span className="v">{statusLabel(detail.lifecycleStatus)}</span></span>
            <span><span className="k">Disposition</span><span className="v">{statusLabel(detail.disposition)}</span></span>
            <span><span className="k">Custody</span><span className="v">{statusLabel(detail.locationKind)}{detail.locationId ? ` · ${referenceNames.location(detail.locationId)}` : ""}</span></span>
            <span><span className="k">Owner</span><span className="v">{referenceNames.party(detail.ownerPartyRef)}</span></span>
            <span><span className="k">Condition</span><span className="v">{statusLabel(detail.conditionGrade)}</span></span>
            <span><span className="k">Overlay flags</span><span className={`v${flags.length ? " bad-text" : " muted"}`}>{flags.length ? flags.map(statusLabel).join(", ") : "None"}</span></span>
            <span><span className="k">Last event seq</span><span className="v">{detail.lastEventSeq.toLocaleString()} · {fmtDateTime(detail.lastEventTime)}</span></span>
          </div>
        )}

        <UnitTabBar search={params} recordId={unitId} counts={{ history: detail?.eventCount, entitlements: entitlementSummary ? `${entitlementSummary.live} live · ${entitlementSummary.closed} closed` : undefined,
          "whats-covered": whatsCovered ? `${whatsCovered.coveredCount} covered · ${whatsCovered.excludedCount} not` : undefined,
          cover: changeCount === undefined ? undefined : `${changeCount} changes`, identifiers: detail?.identifiers.length,
          custody: detail?.episodes.length, asbuilt: detail ? `${detail.asBuilt.filter((r) => !r.removedAt).length} of ${detail.asBuilt.length}` : undefined }} />
        {tab === "questions" && <QuestionsPanel recordKind="unit" recordId={unitId} />}
        {tab === "whats-covered" && <WhatsCoveredPanel unitId={unitId} summary={whatsCovered} />}


        {tab === "entitlements" && <EntitlementPanel unitId={unitId} onSummary={(summary, entitlementRows, reloadRows) => { setEntitlementSummary(summary); setEntitlementDetail(entitlementRows); setReloadEntitlements(() => reloadRows); }} />}

        {tab === "cover" && <CoverSinceSalePanel unitId={unitId} />}

        {tab !== "entitlements" && tab !== "cover" && tab !== "whats-covered" && tab !== "questions" && <div className={`cols-2 unit-tab-content tab-${tab}`}>
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
                      <td className={`primary${i.validTo ? " muted" : ""}`}>{i.type}<span className="sub">{i.validTo ? `superseded${i.correctionReason ? ` · ${i.correctionReason}` : ""}` : `${i.scheme} · scope ${i.scopeKey === "TENANT" ? "tenant" : "item"}${i.issuerPartyRef ? ` · issuer ${referenceNames.party(i.issuerPartyRef)}` : ""}`}</span></td>
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
                      <td className="primary">{e.kind === "ownership" ? `${referenceNames.party(e.partyRef)}` : `${humanise(e.locationKind)}${e.locationId ? ` · ${referenceNames.location(e.locationId)}` : ""}${e.partyRef ? ` · ${referenceNames.party(e.partyRef)}` : ""}`}<span className="sub">{[e.reason ? `reason ${humanise(e.reason)}` : null, e.source ? `source ${humanise(e.source)}` : null, e.confidence ? `confidence ${e.confidence}` : null, e.customerPoRef ? `PO ${e.customerPoRef}` : null, e.dueBack ? `due back ${e.dueBack}` : null].filter(Boolean).join(" · ")}</span></td>
                      <td>{fmtDate(e.validFrom)}<span className="sub">{e.openingDocument ? referenceNames.document(e.openingDocument) : ""}</span></td>
                      <td className={e.validTo ? undefined : "muted"}>{e.validTo ? fmtDate(e.validTo) : "open"}<span className="sub">{e.closingDocument ? referenceNames.document(e.closingDocument) : ""}</span></td>
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
                  <li><span className="node"><span className="item">{detail ? referenceNames.item(detail.itemId) : "…"}</span><span className="serial">{title}</span><span className="hint">· this unit</span></span><StatusBadge tone="neutral">Parent</StatusBadge></li>
                  {detail?.asBuilt.map((a) => (
                    <li className="l1" key={a.linkId}>
                      <span className="node"><span className="slot">{a.slot ?? "—"}</span><span className="item">{a.childItemId ? referenceNames.item(a.childItemId) : "Lot item"}</span>{a.childUnitId ? <UnitLink unitId={a.childUnitId}>{a.childSerial ?? "Open child unit"}</UnitLink> : <span className="serial muted">{a.childLotId ? <>lot {referenceNames.lot(lotDocument(a.childLotId))}</> : ""}{a.quantity != null ? ` · qty ${a.quantity}` : ""}</span>}<span className="hint">· {a.relation}{a.throughPhantomItemId ? ` · through phantom ${referenceNames.item(a.throughPhantomItemId)}` : ""}{a.releaseStatus ? ` · ${a.releaseStatus}` : ""}{a.removedAt ? ` · removed ${fmtDate(a.removedAt)}` : ""}</span></span>
                      <StatusBadge tone={a.removedAt ? "neutral" : a.childUnitId ? "ok" : "neutral"}>{a.removedAt ? "Removed" : a.childUnitId ? "Captured" : "Lot"}</StatusBadge>
                    </li>
                  ))}
                  {detail && detail.asBuilt.length === 0 && <li><span className="node"><span className="hint">No components recorded.</span></span><span /></li>}
                </ul>
              </div>
              <div className="card-footer"><span>{detail?.buildRecords.length ? `${detail.buildRecords.length} build record${detail.buildRecords.length === 1 ? "" : "s"} · latest hash ${detail.buildRecords[0]?.payloadHash.slice(0, 12)}…` : "As-maintained equals as-built until a release freezes it"}</span><Link to="/units/completeness" state={{ serials: title }}>Completeness query</Link></div>
            </section>
            <UnitCustomerMail unitId={unitId} />
          </div>
        </div>}
      </main>
      <Rail label="Lineage">
        <Section title="Lineage">
          <Kv rows={[["Derived from", detail?.derivedFromUnitId ? <UnitLink unitId={detail.derivedFromUnitId}>Open unit</UnitLink> : "—"], ["Merged into", detail?.mergedIntoUnitId ? <UnitLink unitId={detail.mergedIntoUnitId}>Open unit</UnitLink> : "—"], ["Parent", detail?.parentUnitId ? <UnitLink unitId={detail.parentUnitId}>Open unit</UnitLink> : "—"], ["Children", detail ? `${detail.asBuilt.filter((a) => a.childUnitId && !a.removedAt).length} serialised · ${detail.asBuilt.filter((a) => !a.childUnitId && !a.removedAt).length} lot` : "—"], ["Origin", humanise(detail?.origin)], ["Serialisation point", humanise(detail?.serialisationPoint)], ["Retention class", detail?.retentionClass ?? "—"], ["History depth", detail?.historyDepth ?? "—"]]} />
        </Section>
        <Section title="Last events">
          {history?.items.slice(0, 3).map((e) => <div className="job" key={e.eventId}><div><div className="t">{eventTitle(e.eventType)}</div><div className="s">{fmtDateTime(e.eventTime)} · recorded {fmtDateTime(e.recordedTime).slice(11)}</div></div><StatusBadge tone="neutral">seq …{String(e.seq).slice(-4)}</StatusBadge></div>)}
        </Section>
        <Section title="Later phases add here">
          <p className="muted">Workbench entry (P3), RMA and refurbishment (P5), and certificates (P6). Entitlements are now part of the unit record above.</p>
        </Section>
      </Rail>
    </>
  );
}

function lotDocument(id: string | null | undefined) { return id ? { type: "lot", id } : null; }

function eventTitle(code: string): string {
  const tail = code.split(".").pop() ?? code;
  const h = tail.replace(/_/g, " ");
  return h[0]!.toUpperCase() + h.slice(1);
}

function CorrectForm({ unitId, types, onDone }: { unitId: string; types: string[]; onDone: () => void }) {
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
      const r = await units.correctIdentifier({ commandId: newCommandId(), unitId, identifierType: type, newRawValue: value, reason, eventTime: now(), approverRef: approver || null, note: note || null });
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
        <div className="field"><label htmlFor="c-reason">Reason</label><select id="c-reason" className="select" value={reason} onChange={(e) => setReason(e.target.value)}>{REASONS.map((r) => <option key={r} value={r}>{sentence(r)}</option>)}</select></div>
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
  const [flag, setFlag] = useState(FLAGS[0]!);
  const [reason, setReason] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const isSet = flags.includes(flag);
  const submit = async () => {
    setBusy(true); setError(null);
    try {
      const command = { commandId: newCommandId(), unitId, flag, eventTime: now(), reason: reason || null };
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
