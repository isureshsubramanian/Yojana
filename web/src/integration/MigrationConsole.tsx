import { type ReactNode, useCallback, useEffect, useMemo, useState } from "react";
import { Link } from "react-router";
import { DataGrid, KpiStrip, StatusBadge, type GridColumn } from "../components";
import { ApiError } from "../api/client";
import { useSession } from "../session/session";
import { Rail } from "../shell/Drawer";
import { openDrawer, updateDrawer } from "../shell/drawerStore";
import { ErrorNote, Kv, Section } from "../units/common";
import { date, useCursorPage } from "./AdminCommon";
import { MigrationAccess, MigrationPage } from "./MigrationCommon";
import { migrationApi } from "./migrationApi";
import {
  batchLabel, batchResult, batchStatusLabel, batchTotals, countTotal, duration, elapsed, failureLabel,
  finalLoadDisabledReason, loadStatusLabel, loadStatusTone, mayAdministerMigration,
  phaseStatusLabel, phaseStatusTone, profileTotals,
  rollbackOutcomeLabel, routeLabel, runtimeLabel, statusTone,
} from "./migrationModel";
import type {
  CanonicalisationRun, MigrationApply, MigrationBatch,
  MigrationBatchPage, MigrationBatchTable, MigrationMap, MigrationProfile, MigrationReconstruction,
  MigrationSource, MigrationSourceTestResult, MigrationValidation, RollBackMigrationLoadResponse, TouchedMigrationLoadUnit,
} from "./migrationTypes";

export function Component() { return <MigrationAccess><MigrationConsole /></MigrationAccess>; }

function MigrationConsole() {
  const session = useSession();
  const admin = mayAdministerMigration(session.roles);
  const [sources, setSources] = useState<MigrationSource[]>([]); const [sourceId, setSourceId] = useState("");
  const [sourceError, setSourceError] = useState<unknown>(null);
  const [details, setDetails] = useState<Record<string, MigrationBatch>>({});
  const [selected, setSelected] = useState<string | null>(null); const [profile, setProfile] = useState<MigrationProfile | null>(null);
  const [canonicalisation, setCanonicalisation] = useState<CanonicalisationRun | null>(null);
  const [reconstruction, setReconstruction] = useState<MigrationReconstruction | null>(null);
  const [map, setMap] = useState<MigrationMap | null>(null);
  const [validation, setValidation] = useState<MigrationValidation | null>(null);
  const [load, setLoad] = useState<MigrationApply | null>(null);
  const [phaseError, setPhaseError] = useState<unknown>(null);
  const [startBusy, setStartBusy] = useState(false); const [startError, setStartError] = useState<unknown>(null); const [startNotice, setStartNotice] = useState("");
  const fetchBatches = useCallback((cursor?: string, signal?: AbortSignal) => migrationApi.batches(cursor, signal), []);
  const batches = useCursorPage<MigrationBatch, MigrationBatchPage>(fetchBatches);

  const loadSources = useCallback((signal?: AbortSignal) => migrationApi.sources(undefined, signal).then(page => {
    setSources(page.items); setSourceId(current => current || page.items[0]?.sourceId || ""); setSourceError(null);
  }).catch(reason => { if (!(reason instanceof DOMException && reason.name === "AbortError")) setSourceError(reason); }), []);
  useEffect(() => { if (!admin) return; const controller = new AbortController(); void loadSources(controller.signal); return () => controller.abort(); }, [admin, loadSources]);

  useEffect(() => {
    if (!batches.result) return;
    const controller = new AbortController();
    Promise.all(batches.result.items.map(row => migrationApi.batch(row.batchId, controller.signal)))
      .then(rows => { if (!controller.signal.aborted) setDetails(Object.fromEntries(rows.map(row => [row.batchId, row]))); })
      .catch(() => undefined);
    return () => controller.abort();
  }, [batches.result]);

  const source = sources.find(item => item.sourceId === sourceId) ?? null;
  const latest = (admin ? batches.result?.items.find(item => item.sourceId === sourceId) : batches.result?.items[0]) ?? null;
  const latestDetail = latest ? details[latest.batchId] ?? null : null;
  const selectedBatch = selected ? details[selected] ?? batches.result?.items.find(item => item.batchId === selected) ?? null : latestDetail;
  const phaseBatch = selectedBatch ?? latest;
  const phaseBatchId = phaseBatch?.batchId ?? "";
  const loadPhases = useCallback(async (signal?: AbortSignal) => {
    if (!phaseBatchId) return;
    try {
      const [nextBatch, nextProfile, nextCanonicalisation, nextReconstruction, nextMap, nextValidation, nextLoad] = await Promise.all([
        migrationApi.batch(phaseBatchId, signal),
        optional(() => migrationApi.profile(phaseBatchId, signal)),
        optional(() => migrationApi.canonicalisation(phaseBatchId, signal)),
        optional(() => migrationApi.reconstruction(phaseBatchId, signal)),
        optional(() => migrationApi.map(phaseBatchId, signal)),
        optional(() => migrationApi.validation(phaseBatchId, signal)),
        optional(() => migrationApi.load(phaseBatchId, signal)),
      ]);
      if (signal?.aborted) return;
      setDetails(current => ({ ...current, [nextBatch.batchId]: nextBatch }));
      setProfile(nextProfile); setCanonicalisation(nextCanonicalisation); setReconstruction(nextReconstruction);
      setMap(nextMap); setValidation(nextValidation); setLoad(nextLoad); setPhaseError(null);
    } catch (reason) {
      if (!(reason instanceof DOMException && reason.name === "AbortError")) setPhaseError(reason);
    }
  }, [phaseBatchId]);
  useEffect(() => {
    setProfile(null); setCanonicalisation(null); setReconstruction(null); setMap(null); setValidation(null); setLoad(null); setPhaseError(null);
    if (!phaseBatchId) return;
    const controller = new AbortController();
    void loadPhases(controller.signal);
    return () => controller.abort();
  }, [loadPhases, phaseBatchId]);

  const watching = [phaseBatch?.status, profile?.status, canonicalisation?.status, reconstruction?.status, map?.status, validation?.status, load?.status]
    .some(status => status === "queued" || status === "running");
  useEffect(() => {
    if (!watching) return;
    const timer = window.setInterval(() => void loadPhases(), 3000);
    return () => window.clearInterval(timer);
  }, [loadPhases, watching]);

  const flaggedClasses = profile?.classes.filter(row => (row.count ?? 0) > 0).length;
  const totals = phaseBatch?.tables ? batchTotals(phaseBatch.tables) : null;
  const serials = phaseBatch?.tables?.find(row => row.role === "serial_master")?.rowsRead;
  const columns = useMemo<GridColumn<MigrationBatch>[]>(() => [
    ...(!admin || sources.length > 1 ? [{ key: "source", label: "Source", width: 17, render: (row: MigrationBatch) => sources.find(item => item.sourceId === row.sourceId)?.displayLabel ?? row.sourceSystem }] : []),
    { key: "started", label: "Started", width: 16, render: row => date(row.startedAt ?? row.requestedAt) },
    { key: "took", label: "Took", width: 12, render: row => elapsed(row.startedAt, row.finishedAt) },
    { key: "by", label: "Started by", width: 18, render: row => row.requestedByName },
    { key: "status", label: "Status", width: 14, render: row => <StatusBadge tone={statusTone(row.status)}>{batchStatusLabel(row.status)}</StatusBadge> },
    { key: "result", label: "Result", width: !admin || sources.length > 1 ? 23 : 40, render: row => batchResult(details[row.batchId] ?? row) },
  ], [admin, details, sources]);

  const refresh = () => { batches.refresh(); setStartNotice(""); };
  const start = async () => {
    if (!source) return; setStartBusy(true); setStartError(null); setStartNotice("");
    try {
      const result = await migrationApi.startBatch(source.sourceId);
      setStartNotice(result.failure ? `Extraction refused: ${failureLabel(result.failure)}` : "Full extraction queued.");
      batches.refresh();
    } catch (reason) { setStartError(reason); } finally { setStartBusy(false); }
  };
  const showBatch = (batch: MigrationBatch) => {
    setSelected(batch.batchId);
    const detail = details[batch.batchId] ?? batch;
    openDrawer({ eyebrow: "Extraction", title: batchLabel(batch) }, <BatchDrawer batch={detail} source={sources.find(item => item.sourceId === batch.sourceId) ?? null} />);
  };

  const eyebrow = source ? <>{session.tenant?.name} · source: {source.displayLabel} · {runtimeLabel(source.runtime)} · {routeLabel(source.route)}</> : latest ? <>{session.tenant?.name} · source: {latest.sourceSystem}</> : "Migration source";
  const actions = admin ? <>
    {sources.length > 1 && <select className="select migration-source-picker" aria-label="Migration source" value={sourceId} onChange={event => { setSourceId(event.target.value); setSelected(null); }}><option value="">Choose a source</option>{sources.map(item => <option value={item.sourceId} key={item.sourceId}>{item.displayLabel}</option>)}</select>}
    <button type="button" className="btn" disabled={!source} onClick={() => source && void testSource(source)}>Test the source</button>
    <button type="button" className="btn primary" disabled={!source || startBusy} onClick={() => void start()}>{startBusy ? "Starting…" : "Start a full extraction"}</button>
  </> : undefined;

  return <><MigrationPage title="Migration" eyebrow={eyebrow} sub="Copy the installed base, cover and history out of the source application, check it, and sign it off. The source is only ever read." actions={actions} flaggedClasses={flaggedClasses} waitingReviews={canonicalisation?.waitingReviewItems}>
    <ErrorNote error={sourceError ?? batches.error ?? startError ?? phaseError} />
    {startNotice && <div className={`note${startNotice.startsWith("Extraction refused") ? " bad" : ""}`} role="status">{startNotice}</div>}
    {admin && source && <LicenceBanner source={source} onSaved={updated => setSources(current => current.map(item => item.sourceId === updated.sourceId ? updated : item))} />}
    <PhaseStrip key={phaseBatchId || "none"} batch={phaseBatch} profile={profile} canonicalisation={canonicalisation} reconstruction={reconstruction} map={map} validation={validation} load={load} admin={admin} onChanged={loadPhases} />
    {totals && <KpiStrip label="Extraction figures" kpis={[
      ...(serials === undefined ? [] : [{ label: "Serials in the source", value: serials.toLocaleString(), detail: "serial-master rows read" }]),
      { label: "Rows gone from the source", value: totals.absent.toLocaleString(), detail: "kept and marked absent" },
    ]} />}
    <BatchTables batch={selectedBatch} latest={selectedBatch?.batchId === latest?.batchId} />
    <section className="card migration-table-card" aria-labelledby="migration-batches"><div className="card-header"><h2 id="migration-batches">Extractions <span className="meta">newest first</span></h2></div>
      <DataGrid columns={columns} page={batches.grid} rowId={row => row.batchId} onPage={batches.onPage} onRowClick={showBatch} activeRowId={selected ?? latest?.batchId} loading={batches.busy} viewportHeight={430} emptyText="No extractions have been requested." />
    </section>
  </MigrationPage><Rail label="Migration"><Section title="Source"><p className="muted">Extraction uses the configured source connection. Credentials are never shown here, and the server records the signed-in administrator for every change.</p>{source && <Kv rows={[["Source", source.displayLabel], ["Source kind", runtimeLabel(source.runtime)], ["Route", routeLabel(source.route)], ["Connection", source.hasConnectionString ? "Configured" : "Not configured"]]} />}</Section></Rail></>;
}

function LicenceBanner({ source, onSaved }: { source: MigrationSource; onSaved: (source: MigrationSource) => void }) {
  const confirmed = source.licencePosition === "confirmed";
  return <div className={`note migration-licence${confirmed ? "" : " warn"}`} role="status"><span><strong>{confirmed ? "Extraction allowed." : "Extraction is not allowed."}</strong>&nbsp;{confirmed ? <>Licence position confirmed{source.licenceConfirmedAt ? ` on ${date(source.licenceConfirmedAt)}` : ""}{source.licenceConfirmedByName ? ` by ${source.licenceConfirmedByName}` : ""}{source.licenceContractReference ? ` against ${source.licenceContractReference}` : ""}{source.licenceNote ? `: ${source.licenceNote}` : "."}</> : "Confirm the customer's licence position before starting an extraction."}</span><button type="button" className="btn sm" onClick={() => openLicence(source, onSaved)}>{confirmed ? "Change or withdraw" : "Confirm licence position"}</button></div>;
}

function openLicence(source: MigrationSource, onSaved: (source: MigrationSource) => void) {
  openDrawer({ eyebrow: "Migration source", title: "Licence position" }, <LicenceForm source={source} onSaved={onSaved} />);
}

function LicenceForm({ source, onSaved }: { source: MigrationSource; onSaved: (source: MigrationSource) => void }) {
  const [confirmed, setConfirmed] = useState(source.licencePosition === "confirmed"); const [reference, setReference] = useState(source.licenceContractReference ?? ""); const [note, setNote] = useState(source.licenceNote ?? "");
  const [busy, setBusy] = useState(false); const [error, setError] = useState<unknown>(null); const [saved, setSaved] = useState(false);
  const save = async () => { setBusy(true); setError(null); setSaved(false); try { const updated = await migrationApi.licence(source.sourceId, confirmed, confirmed ? reference.trim() : null, note.trim() || null); onSaved(updated); setSaved(true); } catch (reason) { setError(reason); } finally { setBusy(false); } };
  return <form className="migration-drawer-form" onSubmit={event => { event.preventDefault(); void save(); }}><label className="field"><span className="lbl">Position</span><select className="select" value={confirmed ? "confirmed" : "withdrawn"} onChange={event => setConfirmed(event.target.value === "confirmed")}><option value="confirmed">Confirmed</option><option value="withdrawn">Withdrawn</option></select></label><label className="field"><span className="lbl">Customer contract reference</span><input className="input" value={reference} onChange={event => setReference(event.target.value)} disabled={!confirmed} required={confirmed} /></label><label className="field"><span className="lbl">Note</span><textarea className="input" value={note} onChange={event => setNote(event.target.value)} required={confirmed} /></label><p className="muted">The server records who makes this change and when.</p><ErrorNote error={error} />{saved && <span role="status">Licence position saved.</span>}<button className={`btn ${confirmed ? "primary" : "danger"}`} disabled={busy || (confirmed && (!reference.trim() || !note.trim()))}>{busy ? "Saving…" : confirmed ? "Confirm licence position" : "Withdraw confirmation"}</button></form>;
}

async function testSource(source: MigrationSource) {
  openDrawer({ eyebrow: "Migration source", title: "Test results" }, <p className="muted">Testing readable source tables…</p>);
  try { const result = await migrationApi.testSource(source.sourceId); updateDrawer({ eyebrow: "Migration source", title: "Test results" }, <SourceTest result={result} />); }
  catch (reason) { updateDrawer({ eyebrow: "Migration source", title: "Test results" }, <ErrorNote error={reason} />); }
}

function SourceTest({ result }: { result: MigrationSourceTestResult }) {
  return <div className="grid-viewport"><table className="table"><thead><tr><th>Source table</th><th className="num">Rows</th><th>Status</th></tr></thead><tbody>{result.tables.map(row => <tr key={row.table}><td><span className="primary">{row.label ?? row.table}</span><span className="sub mono">{row.table}</span></td><td className="num">{row.rowCount?.toLocaleString() ?? "—"}</td><td><StatusBadge tone={row.error ? "bad" : "ok"}>{row.error ? failureLabel(row.error) : "Readable"}</StatusBadge></td></tr>)}</tbody></table></div>;
}

function PhaseStrip({ batch, profile, canonicalisation, reconstruction, map, validation, load, admin, onChanged }: {
  batch: MigrationBatch | null; profile: MigrationProfile | null; canonicalisation: CanonicalisationRun | null;
  reconstruction: MigrationReconstruction | null; map: MigrationMap | null; validation: MigrationValidation | null;
  load: MigrationApply | null; admin: boolean; onChanged: () => void | Promise<void>;
}) {
  const tables = batch?.tables ?? []; const totals = batchTotals(tables); const profileCount = profileTotals(profile).flagged;
  const reconstructEvents = countTotal(reconstruction?.eventsByMovementKind ?? []);
  const reconstructReviews = countTotal(reconstruction?.queuedForReviewByReason ?? []);
  const mappedUnits = countTotal(map?.unitsByOrigin ?? []);
  const mapGaps = map?.gapsByKind.map(row => <Link key={row.kind} className="migration-phase-link"
    to={`/integration/migration/validation?batchId=${encodeURIComponent(batch?.batchId ?? "")}&gapKind=${encodeURIComponent(row.kind)}#migration-plan-gaps`}>
    {row.label} {row.count.toLocaleString()}</Link>);
  return <section className="card" aria-labelledby="migration-phases"><div className="card-header"><h2 id="migration-phases">Migration phases <span className="meta">{batch ? date(batch.requestedAt) : "not run"}</span></h2><span className="muted">The selected extraction and its latest run in each phase</span></div><div className="card-body"><div className="migration-phases">
    <PhaseCard label="0 · Extract" headline={tables.length ? `${tables.length} tables` : "—"} detail={tables.length ? `${totals.rowsRead.toLocaleString()} rows read · ${elapsed(batch?.startedAt ?? null, batch?.finishedAt ?? null)}` : "No table results yet"} status={batch?.status} href="#migration-tables" />
    <PhaseCard label="1 · Profile" headline={profile ? `${profile.classes.length} classes` : "—"} detail={profile ? `${profileCount.toLocaleString()} rows flagged` : "No profiling run yet"} status={profile?.status} href="/integration/migration/profiling" />
    <PhaseCard label="2 · Canonicalise" headline={canonicalisation ? `${canonicalisation.candidateRowsTotal.toLocaleString()} candidates` : "—"} detail={canonicalisation ? canonicalisation.failure ? failureLabel(canonicalisation.failure)! : `${canonicalisation.waitingReviewItems.toLocaleString()} waiting for review` : "No canonicalisation run yet"} status={canonicalisation?.status} href="/integration/migration/review" />
    <PhaseCard label="3 · Reconstruct" headline={reconstruction ? `${reconstruction.candidatesReconstructed.toLocaleString()} units` : "—"} detail={reconstruction ? reconstruction.failure ? failureLabel(reconstruction.failure)! : `${reconstructEvents.toLocaleString()} events · ${reconstructReviews.toLocaleString()} to review` : "No reconstruction run yet"} status={reconstruction?.status} />
    <PhaseCard label="4 · Map" headline={map ? `${mappedUnits.toLocaleString()} units` : "—"} detail={map ? map.failure ? failureLabel(map.failure)! : mapGaps?.length ? <>{mapGaps}</> : "No mapping gaps" : "No map run yet"} status={map?.status} />
    <PhaseCard label="5 · Validate" headline={validation ? `${validation.hardFailed.toLocaleString()} hard fails open` : "—"} detail={validation ? `${validation.signedChecks.toLocaleString()} of ${validation.checksTotal.toLocaleString()} checks signed` : "No validation run yet"} status={validation?.status} href="/integration/migration/validation" />
    <LoadPhaseCard batchId={batch?.batchId ?? ""} load={load} />
  </div><LoadActions batchId={batch?.batchId ?? ""} load={load} validation={validation} admin={admin} onChanged={onChanged} /></div></section>;
}

function PhaseCard({ label, headline, detail, status, href }: { label: string; headline: string; detail: ReactNode; status: string | null | undefined; href?: string }) {
  const body = <><div className="l">{label}</div><div className="n">{headline}</div><div className="s">{detail}</div><StatusBadge tone={phaseStatusTone(status)}>{phaseStatusLabel(status)}</StatusBadge></>;
  if (href?.startsWith("/")) return <Link className="migration-phase" to={href}>{body}</Link>;
  if (href) return <a className="migration-phase" href={href}>{body}</a>;
  return <div className="migration-phase">{body}</div>;
}

function LoadPhaseCard({ batchId, load }: { batchId: string; load: MigrationApply | null }) {
  const failure = failureLabel(load?.failure ?? null);
  const gapLink = load && load.refused > 0
    ? <Link className="migration-phase-link" to={`/integration/migration/loads/${load.loadId}/refusals`}>View {load.refused.toLocaleString()} refusals</Link>
    : load?.coverGaps !== null && load?.coverGaps !== undefined
      ? <Link className="migration-phase-link" to="/entitlements/gaps?channels=migration">{load.coverGaps.toLocaleString()} sold units with no base cover</Link>
      : load?.gapsByKind[0] ? <Link className="migration-phase-link"
        to={`/integration/migration/validation?batchId=${encodeURIComponent(batchId)}&gapKind=${encodeURIComponent(load.gapsByKind[0].kind)}#migration-plan-gaps`}>
        {load.gapsByKind[0].label} {load.gapsByKind[0].count.toLocaleString()}</Link> : null;
  return <div className="migration-phase migration-load-phase"><div className="l">6 · Load{load ? ` · ${load.rehearsal ? "rehearsal" : "final"}` : ""}</div><div className="n">{load ? `${load.applied.toLocaleString()} applied` : "—"}</div><div className="s">{load ? <>{load.refused.toLocaleString()} refused · {load.eventsWritten.toLocaleString()} facts<br />{load.entitlementsWritten.toLocaleString()} cover · {load.replacementLinksWritten.toLocaleString()} links · {load.certificatesRecorded.toLocaleString()} certificates</> : "No load run yet"}</div><StatusBadge tone={load ? loadStatusTone(load.status) : "neutral"}>{load ? loadStatusLabel(load.status) : "Not run"}</StatusBadge>
    {failure && <p className="migration-phase-note">{failure}</p>}
    {gapLink}
  </div>;
}

function LoadActions({ batchId, load, validation, admin, onChanged }: { batchId: string; load: MigrationApply | null; validation: MigrationValidation | null; admin: boolean; onChanged: () => void | Promise<void> }) {
  const [busy, setBusy] = useState(false); const [error, setError] = useState<unknown>(null); const [notice, setNotice] = useState("");
  const [confirmRollback, setConfirmRollback] = useState(false); const [confirmFinal, setConfirmFinal] = useState(false); const [rollback, setRollback] = useState<RollBackMigrationLoadResponse | null>(null);
  const finalReason = finalLoadDisabledReason(validation);
  const loadOpen = !!load && load.status !== "rolled_back";
  const loadOpenReason = loadOpen ? load?.status === "queued" || load?.status === "running" ? "A load is already in progress" : "Resolve the existing load before loading again" : null;
  const canRollback = !!load?.rehearsal && (load.status === "completed" || load.status === "failed");
  const rehearsalReason = busy ? "Another load action is running" : !batchId ? "Choose an extraction first" : loadOpenReason;
  const finalDisabledReason = busy ? "Another load action is running" : !batchId ? "Choose an extraction first" : loadOpenReason ?? finalReason;
  const rollbackReason = busy ? "Another load action is running" : !load ? "Run a rehearsal load first" : !load.rehearsal ? "A final load cannot be rolled back here" : !canRollback ? `A ${loadStatusLabel(load.status).toLowerCase()} rehearsal cannot be rolled back` : null;
  const start = async (rehearsal: boolean) => {
    if (!batchId) return; setBusy(true); setError(null); setNotice(""); setRollback(null);
    try { await migrationApi.apply(batchId, rehearsal); setConfirmFinal(false); setNotice(rehearsal ? "Rehearsal load queued." : "Final load queued."); await onChanged(); }
    catch (reason) { setError(reason); }
    finally { setBusy(false); }
  };
  const rollBack = async () => {
    if (!load) return; setBusy(true); setError(null); setNotice(""); setRollback(null);
    try { const result = await migrationApi.rollbackLoad(load.loadId); setRollback(result); setConfirmRollback(false); await onChanged(); }
    catch (reason) { setError(reason); }
    finally { setBusy(false); }
  };
  if (!admin) return load ? <div className="migration-load-action-row"><p className="muted">Load requested by {load.requestedByName}.</p></div> : null;
  return <div className="migration-load-action-row" aria-label="Load actions"><div className="migration-load-actions">
    <LoadAction label="Load as rehearsal" reason={rehearsalReason} onClick={() => void start(true)} />
    <LoadAction label="Final load" reason={finalDisabledReason} primary onClick={() => setConfirmFinal(true)} />
    <LoadAction label="Roll back rehearsal" reason={rollbackReason} danger onClick={() => setConfirmRollback(true)} />
  </div>{load && <p className="muted migration-load-by">Latest load requested by {load.requestedByName}.</p>}
    {confirmFinal && <div className="note warn migration-load-confirm" role="group" aria-labelledby="final-confirm-title"><strong id="final-confirm-title">Start the final load?</strong><p>The signed-off plan is written into the module for real. A final load cannot be rolled back.</p><div><button type="button" className="btn sm" disabled={busy} onClick={() => setConfirmFinal(false)}>Not yet</button><button type="button" className="btn primary sm" disabled={busy} onClick={() => void start(false)}>{busy ? "Starting…" : "Start the final load"}</button></div></div>}
    {confirmRollback && <div className="note warn migration-load-confirm" role="group" aria-labelledby="rollback-confirm-title"><strong id="rollback-confirm-title">Roll back this rehearsal?</strong><p>Every unit, fact, cover row, link and certificate this load wrote will be removed.</p><div><button type="button" className="btn sm" disabled={busy} onClick={() => setConfirmRollback(false)}>Keep the load</button><button type="button" className="btn danger sm" disabled={busy} onClick={() => void rollBack()}>{busy ? "Rolling back…" : "Roll back rehearsal"}</button></div></div>}
    <ErrorNote error={error} />{notice && <div className="note" role="status">{notice}</div>}{rollback && <RollbackResult result={rollback} />}
  </div>;
}

function LoadAction({ label, reason, primary, danger, onClick }: { label: string; reason: string | null; primary?: boolean; danger?: boolean; onClick: () => void }) {
  const enabledClass = primary ? " primary" : danger ? " danger" : "";
  return <div className="migration-load-action"><button type="button" className={`btn sm${reason ? "" : enabledClass}`} disabled={!!reason} onClick={onClick}>{label}</button>{reason && <span className="migration-disabled-reason">{reason}.</span>}</div>;
}

function RollbackResult({ result }: { result: RollBackMigrationLoadResponse }) {
  const refused = result.outcome === "refused_touched";
  const [items, setItems] = useState(result.touchedUnits); const [nextCursor, setNextCursor] = useState(result.touchedNextCursor);
  const [page, setPage] = useState(1); const [cursors, setCursors] = useState<(string | undefined)[]>([undefined]); const [busy, setBusy] = useState(false); const [error, setError] = useState<unknown>(null);
  const go = async (target: number) => { const cursor = target > page ? nextCursor ?? undefined : cursors[target - 1]; if (target < 1 || (target > page && !cursor)) return; setBusy(true); setError(null); try { const response = await migrationApi.touchedLoadUnits(result.loadId, cursor); setItems(response.items); setNextCursor(response.nextCursor); setPage(target); setCursors(current => { const next = current.slice(); next[target - 1] = cursor; return next; }); } catch (reason) { setError(reason); } finally { setBusy(false); } };
  const columns: GridColumn<TouchedMigrationLoadUnit>[] = [{ key: "serial", label: "Touched serial", width: 100, mono: true, render: row => row.serial }];
  return <div className={`note${refused ? " warn" : ""}`} role="status"><strong>{rollbackOutcomeLabel(result.outcome)}.</strong>&nbsp;{refused
    ? `${result.touchedUnitCount.toLocaleString()} touched unit${result.touchedUnitCount === 1 ? "" : "s"} stop the rollback. The rehearsal load stays in place.`
    : `${result.unitsRemoved.toLocaleString()} units, ${result.eventsRemoved.toLocaleString()} facts, ${result.entitlementsRemoved.toLocaleString()} cover rows, ${result.replacementLinksRemoved.toLocaleString()} links and ${result.certificatesRemoved.toLocaleString()} certificates removed.`}
    {refused && <div className="migration-touched-grid"><ErrorNote error={error} /><DataGrid columns={columns} page={{ rows: items, total: result.touchedUnitCount, page, pageSize: 50, continuation: nextCursor, totalIsExact: true, hasMore: !!nextCursor }} rowId={row => row.serial} onPage={target => void go(target)} loading={busy} emptyText="No touched serials were returned." viewportHeight={260} /></div>}
  </div>;
}

function BatchTables({ batch, latest }: { batch: MigrationBatch | null; latest: boolean }) {
  const tables = batch?.tables ?? [];
  const columns: GridColumn<MigrationBatchTable>[] = [
    { key: "table", label: "Source table", width: 26, render: row => <span className="migration-table-name primary"><span className="t">{row.label}</span><span className="sub">{row.table}</span></span> },
    { key: "read", label: "Rows read", width: 12, numeric: true, render: row => row.rowsRead.toLocaleString() },
    { key: "new", label: "New", width: 10, numeric: true, render: row => row.inserted.toLocaleString() },
    { key: "changed", label: "Changed", width: 10, numeric: true, render: row => row.updated.toLocaleString() },
    { key: "unchanged", label: "Unchanged", width: 12, numeric: true, render: row => row.unchanged.toLocaleString() },
    { key: "gone", label: "Gone", width: 10, numeric: true, render: row => row.absent.toLocaleString() },
    { key: "time", label: "Time", width: 20, numeric: true, render: row => duration(row.durationMilliseconds) },
  ];
  return <section className="card migration-table-card" id="migration-tables" aria-labelledby="migration-tables-heading"><div className="card-header"><h2 id="migration-tables-heading">What {batch ? batchLabel(batch, latest) : "the latest extraction"} read <span className="meta">{tables.length ? `${tables.length} source tables` : "select an extraction"}</span></h2></div><DataGrid columns={columns} page={{ rows: tables, total: tables.length, page: 1, pageSize: Math.max(1, tables.length), continuation: null }} rowId={row => row.table} onPage={() => undefined} emptyText="No table results are available for this extraction." viewportHeight={520} footer={<span className="muted">Rows gone from the source are kept and marked absent; they are never deleted.</span>} /></section>;
}

function BatchDrawer({ batch, source }: { batch: MigrationBatch; source: MigrationSource | null }) {
  const totals = batch.tables ? batchTotals(batch.tables) : null;
  return <><Kv rows={[["Source", source?.displayLabel ?? batch.sourceSystem], ["Started", date(batch.startedAt ?? batch.requestedAt)], ["Finished", date(batch.finishedAt)], ["Took", elapsed(batch.startedAt, batch.finishedAt)], ["Started by", batch.requestedByName], ["Status", batchStatusLabel(batch.status)], ["Rows", totals ? `${totals.rowsRead.toLocaleString()} read · ${totals.inserted.toLocaleString()} new · ${totals.updated.toLocaleString()} changed · ${totals.absent.toLocaleString()} gone` : "—"]]} />{batch.failure && <div className="note bad">{failureLabel(batch.failure)}</div>}</>;
}

async function optional<T>(action: () => Promise<T>): Promise<T | null> {
  try { return await action(); }
  catch (reason) { if (reason instanceof ApiError && reason.status === 404) return null; throw reason; }
}
