import { useCallback, useEffect, useMemo, useState } from "react";
import { DataGrid, KpiStrip, StatusBadge, type GridColumn } from "../components";
import { ApiError } from "../api/client";
import { useSession } from "../session/session";
import { Rail } from "../shell/Drawer";
import { openDrawer, updateDrawer } from "../shell/drawerStore";
import { ErrorNote, Section } from "../units/common";
import { date, useCursorPage } from "./AdminCommon";
import { MigrationAccess, MigrationPage } from "./MigrationCommon";
import { migrationApi } from "./migrationApi";
import {
  OUTCOMES, batchLabel, factLabel, factValue, mayAdministerMigration, outcomeLabel, outcomeTone, profileClass, profileStatusLabel,
  profileTotals, sampleExample, signedChange, statusTone,
} from "./migrationModel";
import type {
  CanonicalisationRun, MigrationBatch, MigrationBatchPage, MigrationOutcome, MigrationOutcomeChoice,
  MigrationOutcomeSettings, MigrationProfile, MigrationProfileClass, MigrationProfileSample,
} from "./migrationTypes";

export function Component() { return <MigrationAccess><ProfilingScreen /></MigrationAccess>; }

function ProfilingScreen() {
  const admin = mayAdministerMigration(useSession().roles);
  const fetchBatches = useCallback((cursor?: string, signal?: AbortSignal) => migrationApi.batches(cursor, signal), []);
  const batches = useCursorPage<MigrationBatch, MigrationBatchPage>(fetchBatches);
  const [batchId, setBatchId] = useState(""); const [profile, setProfile] = useState<MigrationProfile | null>(null);
  const [canonicalisation, setCanonicalisation] = useState<CanonicalisationRun | null>(null);
  const [samples, setSamples] = useState<Record<string, MigrationProfileSample[]>>({});
  const [loading, setLoading] = useState(false); const [error, setError] = useState<unknown>(null);
  const [outcome, setOutcome] = useState<MigrationOutcome | "">(""); const [rerunBusy, setRerunBusy] = useState(false); const [notice, setNotice] = useState("");

  useEffect(() => {
    if (batches.result?.items[0] && !batches.result.items.some(row => row.batchId === batchId)) setBatchId(batches.result.items[0].batchId);
  }, [batchId, batches.result]);
  useEffect(() => {
    if (!batchId) { setProfile(null); setCanonicalisation(null); setSamples({}); return; }
    const controller = new AbortController(); setLoading(true); setError(null); setSamples({});
    migrationApi.profile(batchId, controller.signal).then(next => { if (!controller.signal.aborted) setProfile(next); }).catch(reason => {
      if (!controller.signal.aborted) { if (reason instanceof ApiError && reason.status === 404) setProfile(null); else setError(reason); }
    }).finally(() => { if (!controller.signal.aborted) setLoading(false); });
    return () => controller.abort();
  }, [batchId]);
  useEffect(() => {
    if (!batchId) return;
    const controller = new AbortController();
    setCanonicalisation(null);
    migrationApi.canonicalisation(batchId, controller.signal).then(setCanonicalisation).catch(reason => {
      if (!controller.signal.aborted && reason instanceof ApiError && reason.status === 404) setCanonicalisation(null);
    });
    return () => controller.abort();
  }, [batchId]);
  useEffect(() => {
    if (!profile || !/^(completed|blocked)$/.test(profile.status)) return;
    const controller = new AbortController();
    Promise.all(profile.classes.map(row => migrationApi.samples(profile.batchId, row.code, controller.signal)
      .then(result => [row.code, result.samples] as const).catch(() => [row.code, []] as const)))
      .then(results => { if (!controller.signal.aborted) setSamples(Object.fromEntries(results)); });
    return () => controller.abort();
  }, [profile?.runId]);

  const currentBatch = batches.result?.items.find(row => row.batchId === batchId) ?? null;
  const totals = profileTotals(profile); const flaggedClasses = profile?.classes.filter(row => (row.count ?? 0) > 0).length;
  const visible = profile?.classes.filter(row => !outcome || row.outcome === outcome) ?? [];
  const blockers = profile?.classes.filter(row => row.outcome === "stop_load" && (row.count ?? 0) > 0) ?? [];
  const columns = useMemo<GridColumn<MigrationProfileClass>[]>(() => [
    { key: "class", label: "Class", width: 30, render: row => { const labels = profileClass(row.code); return <span className="primary"><span className="t">{labels.label}</span><span className="sub">{labels.explanation}</span></span>; } },
    { key: "rows", label: "Rows", width: 10, numeric: true, render: row => row.count === null ? <span title={row.note ?? "This query is not configured."}>Not configured<span className="sub">{row.note}</span></span> : row.count.toLocaleString() },
    { key: "change", label: "Change", width: 10, numeric: true, render: row => <span className={row.change === 0 ? "migration-change-zero" : undefined}>{signedChange(row.change)}</span> },
    { key: "outcome", label: "The load will", width: 18, render: row => <StatusBadge tone={outcomeTone(row.outcome)}>{outcomeLabel(row.outcome)}</StatusBadge> },
    { key: "example", label: "Example", width: 32, render: row => <span className={samples[row.code]?.[0] ? undefined : "muted"}>{sampleExample(samples[row.code]?.[0])}</span> },
  ], [samples]);

  const rerun = async () => {
    if (!batchId) return; setRerunBusy(true); setError(null); setNotice("");
    try { const queued = await migrationApi.rerunProfile(batchId); setProfile(current => current ? { ...current, runId: queued.runId, status: queued.status, startedAt: null, finishedAt: null } : null); setNotice("Profiling queued."); }
    catch (reason) { setError(reason); } finally { setRerunBusy(false); }
  };
  const showClass = (row: MigrationProfileClass) => {
    const rows = samples[row.code];
    openDrawer({ eyebrow: "Anomaly class", title: profileClass(row.code).label }, rows ? <ClassDrawer row={row} samples={rows} /> : <p className="muted">Loading sample rows…</p>);
    if (!rows && batchId) migrationApi.samples(batchId, row.code).then(result => updateDrawer({ eyebrow: "Anomaly class", title: profileClass(row.code).label }, <ClassDrawer row={row} samples={result.samples} />)).catch(reason => updateDrawer({ eyebrow: "Anomaly class", title: profileClass(row.code).label }, <ErrorNote error={reason} />));
  };
  const openOutcomes = async () => {
    openDrawer({ eyebrow: "Profiling", title: "Change outcomes" }, <p className="muted">Loading tenant outcomes…</p>);
    try { const settings = await migrationApi.outcomes(); updateDrawer({ eyebrow: "Profiling", title: "Change outcomes" }, <OutcomesForm initial={settings} />); }
    catch (reason) { updateDrawer({ eyebrow: "Profiling", title: "Change outcomes" }, <ErrorNote error={reason} />); }
  };

  const actions = admin ? <><button type="button" className="btn" onClick={() => void openOutcomes()}>Change outcomes</button><button type="button" className="btn primary" disabled={!batchId || rerunBusy} onClick={() => void rerun()}>{rerunBusy ? "Queuing…" : "Re-run profiling"}</button></> : undefined;
  return <><MigrationPage title="Profiling results" eyebrow={currentBatch ? <>{batchLabel(currentBatch, batches.grid.page === 1 && batches.result?.items[0]?.batchId === currentBatch.batchId)} · {date(currentBatch.requestedAt)} · fixed query pack, run after every extraction</> : "Fixed query pack, run after every extraction"} sub="What is wrong with the source data before anything is loaded. Each class says what the load will do with it." actions={actions} flaggedClasses={flaggedClasses} waitingReviews={canonicalisation?.waitingForPerson}>
    <div className="toolbar"><label className="field"><span className="lbl">Extraction</span><select className="select" value={batchId} onChange={event => setBatchId(event.target.value)}><option value="">Choose an extraction</option>{batches.result?.items.map((row, index) => <option value={row.batchId} key={row.batchId}>{batchLabel(row, batches.grid.page === 1 && index === 0)} · {date(row.requestedAt)}</option>)}</select></label><button type="button" className="btn sm" disabled={batches.grid.page === 1 || batches.busy} onClick={() => batches.onPage(batches.grid.page - 1)}>Newer extractions</button><button type="button" className="btn sm" disabled={!batches.grid.hasMore || batches.busy} onClick={() => batches.onPage(batches.grid.page + 1)}>Older extractions</button></div>
    <ErrorNote error={batches.error ?? error} />{notice && <div className="note" role="status">{notice}</div>}
    {profile?.status === "blocked" && <div className="note bad" role="alert"><strong>The load is stopped:</strong>&nbsp;{blockers.map(row => profileClass(row.code).label).join(", ") || "the profiling run found a blocking anomaly"}.</div>}
    {profile && <KpiStrip label="Profiling figures" kpis={[
      { label: "Rows flagged", value: totals.flagged.toLocaleString(), detail: totals.previous === null ? "No previous extraction comparison" : `was ${totals.previous.toLocaleString()} in the previous extraction` },
      ...OUTCOMES.map(value => ({ label: outcomeLabel(value), value: totals.outcomes[value].toLocaleString() })),
    ]} />}
    <section className="card migration-profile-card" aria-labelledby="profile-classes"><div className="card-header"><h2 id="profile-classes">Anomaly classes <span className="meta">{profile ? `${profile.classes.length} · click one for sample rows` : loading ? "loading" : "not run"}</span></h2>{profile && <StatusBadge tone={statusTone(profile.status)}>{profileStatusLabel(profile.status)}</StatusBadge>}</div>
      <div className="migration-outcome-chips" role="group" aria-label="Outcome filter"><button type="button" className="btn sm" aria-pressed={!outcome} onClick={() => setOutcome("")}>Any outcome</button>{OUTCOMES.map(value => <button type="button" className="btn sm" aria-pressed={outcome === value} onClick={() => setOutcome(value)} key={value}>{outcomeLabel(value)}</button>)}</div>
      <DataGrid columns={columns} page={{ rows: visible, total: visible.length, page: 1, pageSize: Math.max(1, visible.length), continuation: null }} rowId={row => row.code} onPage={() => undefined} onRowClick={showClass} loading={loading} viewportHeight={560} emptyText={profile ? "No anomaly classes match this outcome." : "Profiling has not run for this extraction."} footer={<span className="muted">Change compares with the previous extraction; zero is muted.</span>} />
    </section>
  </MigrationPage><Rail label="Profiling"><Section title="Fixed query pack"><p className="muted">The same fourteen queries run after every extraction. A class outcome is a tenant setting; changing it affects the next profiling run.</p></Section></Rail></>;
}

function ClassDrawer({ row, samples }: { row: MigrationProfileClass; samples: MigrationProfileSample[] }) {
  const labels = profileClass(row.code); const factKeys = [...new Set(samples.flatMap(sample => Object.keys(sample.facts)))];
  return <><p className="muted">{labels.explanation} {labels.action}</p>{row.note && <div className="note">{row.note}</div>}<p><StatusBadge tone={outcomeTone(row.outcome)}>{outcomeLabel(row.outcome)}</StatusBadge></p><div className="grid-viewport"><table className="table migration-sample-table"><thead><tr><th>Serial</th><th>Item</th>{factKeys.map(key => <th key={key}>{factLabel(key)}</th>)}</tr></thead><tbody>{samples.map((sample, index) => <tr key={`${sample.sourceKey}-${index}`}><td className="mono">{sample.serial ?? "—"}</td><td className="mono">{sample.item ?? "—"}</td>{factKeys.map(key => <td key={key}>{factValue(sample.facts[key] ?? null)}</td>)}</tr>)}</tbody></table></div>{!samples.length && <p className="muted">No sample rows are available.</p>}<p className="muted">Showing {samples.length.toLocaleString()} of {row.count?.toLocaleString() ?? "an unconfigured total"}.</p></>;
}

function OutcomesForm({ initial }: { initial: MigrationOutcomeSettings }) {
  const [classes, setClasses] = useState<MigrationOutcomeChoice[]>(initial.classes); const [version, setVersion] = useState(initial.version);
  const [busy, setBusy] = useState(false); const [error, setError] = useState<unknown>(null); const [saved, setSaved] = useState(false);
  const change = (classCode: string, outcome: MigrationOutcome) => setClasses(current => current.map(row => row.classCode === classCode ? { ...row, outcome } : row));
  const save = async () => { setBusy(true); setError(null); setSaved(false); try { const next = await migrationApi.saveOutcomes(classes, version); setClasses(next.classes); setVersion(next.version); setSaved(true); } catch (reason) { setError(reason); } finally { setBusy(false); } };
  return <form className="migration-drawer-form" onSubmit={event => { event.preventDefault(); void save(); }}><p className="muted">Choose what the load does for each anomaly class. These choices are kept per tenant.</p><div>{classes.map(row => { const labels = profileClass(row.classCode); const fixed = labels.hardStop === true; return <label className="migration-outcome-row" key={row.classCode}><span><span className="primary">{labels.label}</span>{fixed && <span className="sub">Fixed at Stop the load because loading it would break unit quantity or location.</span>}</span><select className="select" value={row.outcome} disabled={fixed} aria-label={`${labels.label} outcome`} onChange={event => change(row.classCode, event.target.value as MigrationOutcome)}>{OUTCOMES.map(value => <option value={value} key={value}>{outcomeLabel(value)}</option>)}</select></label>; })}</div><ErrorNote error={error} />{saved && <span role="status">Profiling outcomes saved.</span>}<button className="btn primary" disabled={busy}>{busy ? "Saving…" : "Save outcomes"}</button></form>;
}
