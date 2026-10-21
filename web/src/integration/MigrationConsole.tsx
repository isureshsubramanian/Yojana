import { useCallback, useEffect, useMemo, useState } from "react";
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
  batchLabel, batchResult, batchStatusLabel, batchTotals, duration, elapsed, failureLabel, profileStatusLabel,
  mayAdministerMigration, profileTotals, routeLabel, runtimeLabel, statusTone,
} from "./migrationModel";
import type { CanonicalisationRun, MigrationBatch, MigrationBatchPage, MigrationBatchTable, MigrationProfile, MigrationSource, MigrationSourceTestResult } from "./migrationTypes";

export function Component() { return <MigrationAccess><MigrationConsole /></MigrationAccess>; }

function MigrationConsole() {
  const session = useSession();
  const admin = mayAdministerMigration(session.roles);
  const [sources, setSources] = useState<MigrationSource[]>([]); const [sourceId, setSourceId] = useState("");
  const [sourceError, setSourceError] = useState<unknown>(null);
  const [details, setDetails] = useState<Record<string, MigrationBatch>>({});
  const [selected, setSelected] = useState<string | null>(null); const [profile, setProfile] = useState<MigrationProfile | null>(null);
  const [canonicalisation, setCanonicalisation] = useState<CanonicalisationRun | null>(null);
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
  useEffect(() => {
    if (!latest) { setProfile(null); setCanonicalisation(null); return; }
    const controller = new AbortController();
    setCanonicalisation(null);
    migrationApi.profile(latest.batchId, controller.signal).then(setProfile).catch(reason => {
      if (!controller.signal.aborted && reason instanceof ApiError && reason.status === 404) setProfile(null);
    });
    migrationApi.canonicalisation(latest.batchId, controller.signal).then(setCanonicalisation).catch(reason => {
      if (!controller.signal.aborted && reason instanceof ApiError && reason.status === 404) setCanonicalisation(null);
    });
    return () => controller.abort();
  }, [latest?.batchId]);

  const flaggedClasses = profile?.classes.filter(row => (row.count ?? 0) > 0).length;
  const totals = latestDetail?.tables ? batchTotals(latestDetail.tables) : null;
  const serials = latestDetail?.tables?.find(row => row.role === "serial_master")?.rowsRead;
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
    <ErrorNote error={sourceError ?? batches.error ?? startError} />
    {startNotice && <div className={`note${startNotice.startsWith("Extraction refused") ? " bad" : ""}`} role="status">{startNotice}</div>}
    {admin && source && <LicenceBanner source={source} onSaved={updated => setSources(current => current.map(item => item.sourceId === updated.sourceId ? updated : item))} />}
    <PhaseStrip batch={latestDetail ?? latest} profile={profile} />
    {totals && <KpiStrip label="Latest extraction figures" kpis={[
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

function PhaseStrip({ batch, profile }: { batch: MigrationBatch | null; profile: MigrationProfile | null }) {
  const tables = batch?.tables ?? []; const totals = batchTotals(tables); const profileCount = profileTotals(profile).flagged;
  return <section className="card" aria-labelledby="migration-phases"><div className="card-header"><h2 id="migration-phases">Latest extraction <span className="meta">{batch ? date(batch.requestedAt) : "not run"}</span></h2><span className="muted">Extract and Profile run on every full extraction</span></div><div className="card-body"><div className="migration-phases">
    <div className="migration-phase"><div className="l">0 · Extract</div><div className="n">{tables.length ? `${tables.length} tables` : "—"}</div><div className="s">{tables.length ? `${totals.rowsRead.toLocaleString()} rows read · ${elapsed(batch?.startedAt ?? null, batch?.finishedAt ?? null)}` : "No table results yet"}</div><StatusBadge tone={statusTone(batch?.status ?? "waiting")}>{batch ? batchStatusLabel(batch.status) : "Not run"}</StatusBadge></div>
    <div className="migration-phase"><div className="l">1 · Profile</div><div className="n">{profile ? `${profile.classes.length} classes` : "—"}</div><div className="s">{profile ? `${profileCount.toLocaleString()} rows flagged` : "No profiling run yet"}</div><StatusBadge tone={statusTone(profile?.status ?? "waiting")}>{profile ? profileStatusLabel(profile.status) : "Not run"}</StatusBadge></div>
    {(["Canonicalise", "Reconstruct", "Map", "Validate"] as const).map((label, index) => <div className="migration-phase not-built" key={label}><div className="l">{index + 2} · {label}</div><div className="n">—</div><div className="s">Not built yet</div><StatusBadge tone="neutral">Not built yet</StatusBadge></div>)}
  </div></div></section>;
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
  return <section className="card migration-table-card" aria-labelledby="migration-tables"><div className="card-header"><h2 id="migration-tables">What {batch ? batchLabel(batch, latest) : "the latest extraction"} read <span className="meta">{tables.length ? `${tables.length} source tables` : "select an extraction"}</span></h2></div><DataGrid columns={columns} page={{ rows: tables, total: tables.length, page: 1, pageSize: Math.max(1, tables.length), continuation: null }} rowId={row => row.table} onPage={() => undefined} emptyText="No table results are available for this extraction." viewportHeight={520} footer={<span className="muted">Rows gone from the source are kept and marked absent; they are never deleted.</span>} /></section>;
}

function BatchDrawer({ batch, source }: { batch: MigrationBatch; source: MigrationSource | null }) {
  const totals = batch.tables ? batchTotals(batch.tables) : null;
  return <><Kv rows={[["Source", source?.displayLabel ?? batch.sourceSystem], ["Started", date(batch.startedAt ?? batch.requestedAt)], ["Finished", date(batch.finishedAt)], ["Took", elapsed(batch.startedAt, batch.finishedAt)], ["Started by", batch.requestedByName], ["Status", batchStatusLabel(batch.status)], ["Rows", totals ? `${totals.rowsRead.toLocaleString()} read · ${totals.inserted.toLocaleString()} new · ${totals.updated.toLocaleString()} changed · ${totals.absent.toLocaleString()} gone` : "—"]]} />{batch.failure && <div className="note bad">{failureLabel(batch.failure)}</div>}</>;
}
