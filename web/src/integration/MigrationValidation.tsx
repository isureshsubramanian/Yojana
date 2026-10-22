import { useCallback, useEffect, useMemo, useState } from "react";
import { ApiError } from "../api/client";
import { DataGrid, KpiStrip, StatusBadge, type GridColumn } from "../components";
import { useSession } from "../session/session";
import { Rail } from "../shell/Drawer";
import { openDrawer, updateDrawer } from "../shell/drawerStore";
import { Kv, Section } from "../units/common";
import { fmtDate, fmtDateTime } from "../units/format";
import { date, useCursorPage } from "./AdminCommon";
import { MigrationAccess, MigrationPage } from "./MigrationCommon";
import { migrationApi } from "./migrationApi";
import {
  batchLabel, compactNumber, duration, mayAdministerMigration, mayPrepareValidation, mayReviewValidation,
  percentage, refusalRuleLabel, signoffDisabledReason, signedChange, validationAttentionCount,
  validationCheckLabel, validationDifferenceLabel, validationLimitLabel, validationResultLabel,
  validationResultTone, validationRoleLabel, validationTrail, validationTrailLine,
} from "./migrationModel";
import type {
  MigrationBatch, MigrationBatchPage, MigrationValidation as MigrationValidationView, SignoffRecord,
  ValidationCheck, ValidationCheckDetail, ValidationDifferencePage, ValidationResult, ValidationSample,
  ValidationSampleOutcome, ValidationSamplePage, ValidationSettings, ValidationSignatureRole,
} from "./migrationTypes";

export function Component() { return <MigrationAccess><MigrationValidation /></MigrationAccess>; }

function MigrationValidation() {
  const session = useSession();
  const admin = mayAdministerMigration(session.roles);
  const fetchBatches = useCallback((cursor?: string, signal?: AbortSignal) => migrationApi.batches(cursor, signal), []);
  const batches = useCursorPage<MigrationBatch, MigrationBatchPage>(fetchBatches);
  const [batchId, setBatchId] = useState("");
  const [run, setRun] = useState<MigrationValidationView | null>(null);
  const [settings, setSettings] = useState<ValidationSettings | null>(null);
  const [loading, setLoading] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [notice, setNotice] = useState("");
  const [signoff, setSignoff] = useState<SignoffRecord | null>(null);

  useEffect(() => {
    const available = batches.result?.items;
    if (available?.length) setBatchId(current => available.some(batch => batch.batchId === current) ? current : available[0]!.batchId);
  }, [batches.result]);

  useEffect(() => {
    const controller = new AbortController();
    migrationApi.validationSettings(controller.signal).then(value => { if (!controller.signal.aborted) setSettings(value); })
      .catch(reason => { if (!controller.signal.aborted) setError(reason); });
    return () => controller.abort();
  }, []);

  const loadValidation = useCallback(async (signal?: AbortSignal) => {
    if (!batchId) { setRun(null); return; }
    setLoading(true); setError(null); setSignoff(null);
    try { setRun(await migrationApi.validation(batchId, signal)); }
    catch (reason) {
      if (reason instanceof ApiError && reason.status === 404) setRun(null);
      else if (!(reason instanceof DOMException && reason.name === "AbortError")) setError(reason);
    } finally { if (!signal?.aborted) setLoading(false); }
  }, [batchId]);

  useEffect(() => {
    const controller = new AbortController();
    void loadValidation(controller.signal);
    return () => controller.abort();
  }, [loadValidation]);

  const refresh = useCallback(() => loadValidation(), [loadValidation]);
  const currentBatch = batches.result?.items.find(batch => batch.batchId === batchId) ?? null;
  const latest = batches.grid.page === 1 && batches.result?.items[0]?.batchId === batchId;

  // The final load needs the sign-off record of a FINAL validation run, so the other mode is always one click away.
  const rerun = async (rehearsal: boolean) => {
    if (!batchId) return;
    setBusy(true); setError(null); setNotice("");
    try {
      await migrationApi.runValidation(batchId, rehearsal);
      setNotice(rehearsal ? "Rehearsal validation checks queued." : "Final validation checks queued.");
      await loadValidation();
    } catch (reason) { setError(reason); }
    finally { setBusy(false); }
  };

  const createSignoff = async () => {
    if (!run) return;
    setBusy(true); setError(null); setNotice("");
    try {
      const record = await migrationApi.createSignoff(run.runId);
      setSignoff(record);
      setNotice(`${record.rehearsal ? "REHEARSAL" : "FINAL"} sign-off record created by ${record.createdByName}.`);
    } catch (reason) { setError(reason); }
    finally { setBusy(false); }
  };

  const disabledReason = signoff ? "A sign-off record was created for this run" : signoffDisabledReason(run);
  const actions = admin ? <>
    <button type="button" className="btn" disabled={busy || !batchId} onClick={() => void rerun(run?.rehearsal ?? true)}>{busy ? "Working…" : "Run checks again"}</button>
    <button type="button" className="btn" disabled={busy || !batchId} title={run?.rehearsal === false ? "Runs the checks as a rehearsal; its sign-off record is stamped REHEARSAL" : "Runs the checks for the final load; its sign-off record lets the final load start"} onClick={() => void rerun(run?.rehearsal === false)}>{run?.rehearsal === false ? "Run rehearsal checks" : "Run final checks"}</button>
    <button type="button" className="btn primary" disabled={busy || !!disabledReason} title={disabledReason ?? (run?.rehearsal ? "Creates a REHEARSAL record" : "Creates a FINAL record")} onClick={() => void createSignoff()}>Create sign-off record</button>
  </> : undefined;

  const eyebrow = run && currentBatch
    ? <>{run.rehearsal ? "Rehearsal" : "Final validation"} · {batchLabel(currentBatch, latest)} · cutover needs every check signed by a preparer and a reviewer</>
    : currentBatch ? <>{batchLabel(currentBatch, latest)} · no validation run yet</> : "Validation and sign-off";
  const unit = run?.checks.find(check => check.code === "unit_master") ?? null;
  const history = run?.checks.find(check => check.code === "historical_events") ?? null;
  const targetLabel = run?.targetKind === "module" ? "Module" : "Plan";

  const columns = useMemo<GridColumn<ValidationCheck>[]>(() => [
    { key: "check", label: "Check", width: 24, render: check => <span className="primary"><span className="t">{validationCheckLabel(check.code, check.label)}</span><span className="sub">{check.howCounted}</span></span> },
    { key: "limit", label: "Limit", width: 9, render: check => validationLimitLabel(check.limitKind) },
    { key: "source", label: "Source", width: 9, numeric: true, render: check => total(check.sourceTotal) },
    { key: "target", label: targetLabel, width: 9, numeric: true, render: check => total(check.targetTotal) },
    { key: "difference", label: "Difference", width: 9, numeric: true, render: check => <span className={check.difference === 0 ? "migration-diff-ok" : check.difference === null ? undefined : "migration-diff-bad"}>{difference(check)}</span> },
    { key: "result", label: "Result", width: 10, render: check => <StatusBadge tone={validationResultTone(check.result)}>{validationResultLabel(check.result)}</StatusBadge> },
    { key: "signers", label: "Prepared · reviewed", width: 18, render: check => signatureNames(check) },
    { key: "signed", label: "Signed", width: 12, render: check => signedDate(check) },
  ], [targetLabel]);

  const openCheck = useCallback(async (check: ValidationCheck) => {
    const title = validationCheckLabel(check.code, check.label);
    openDrawer({ eyebrow: "Validation check", title }, <p className="muted">Loading check details…</p>);
    try {
      const [detail, differences] = await Promise.all([
        migrationApi.validationCheck(check.checkId), migrationApi.validationDifferences(check.checkId),
      ]);
      updateDrawer({ eyebrow: "Validation check", title }, <CheckDrawer initial={detail} initialDifferences={differences} runId={run!.runId} roles={session.roles} targetLabel={targetLabel} onChanged={refresh} />);
    } catch (reason) {
      updateDrawer({ eyebrow: "Validation check", title }, <ValidationError error={reason} />);
    }
  }, [refresh, run, session.roles, targetLabel]);

  return <><MigrationPage title="Validation and sign-off" eyebrow={eyebrow}
    sub={<>Each check counts the same thing in the source and in the {targetLabel.toLowerCase()}. Zero tolerance where the rule says so; history within {settings ? percentage(settings.historicalTolerancePercent) : "the tenant limit"}.</>}
    actions={actions} validationAttention={validationAttentionCount(run)}>
    <div className="toolbar migration-review-toolbar"><label className="field"><span className="lbl">Extraction</span><select className="select" value={batchId} onChange={event => setBatchId(event.target.value)}><option value="">Choose an extraction</option>{batches.result?.items.map((batch, index) => <option value={batch.batchId} key={batch.batchId}>{batchLabel(batch, batches.grid.page === 1 && index === 0)} · {date(batch.requestedAt)}</option>)}</select></label><button type="button" className="btn sm" disabled={batches.grid.page === 1 || batches.busy} onClick={() => batches.onPage(batches.grid.page - 1)}>Newer extractions</button><button type="button" className="btn sm" disabled={!batches.grid.hasMore || batches.busy} onClick={() => batches.onPage(batches.grid.page + 1)}>Older extractions</button></div>
    <ValidationError error={batches.error ?? error} />
    {notice && <div className="note" role="status"><strong>{signoff?.rehearsal ? "REHEARSAL · " : signoff ? "FINAL · " : ""}</strong>{notice}{signoff && <> <a className="btn sm" href={migrationApi.signoffExportUrl(signoff.recordId)} download>CSV</a></>}</div>}
    {run?.hardFailed ? <div className="note migration-hard-fail" role="alert"><strong>{run.hardFailed.toLocaleString()} hard fail{run.hardFailed === 1 ? " stops" : "s stop"} the cutover.</strong>&nbsp;{hardFailFacts(run.checks)} Fix them in the incumbent; the next extraction and rehearsal will check again.</div> : null}
    {run && <KpiStrip label="Validation figures" kpis={[
      { label: "Checks", value: run.checksTotal.toLocaleString(), detail: `${run.passed.toLocaleString()} pass · ${run.explained.toLocaleString()} explained · ${run.failed.toLocaleString()} fail` },
      { label: "Signed off", value: `${run.signedChecks.toLocaleString()} of ${run.checksTotal.toLocaleString()}`, detail: "preparer and reviewer" },
      { label: "Units loaded", value: run.units.toLocaleString(), detail: `source ${total(unit?.sourceTotal ?? null)} · difference ${difference(unit)}` },
      { label: "History events", value: compactNumber(run.events), detail: `${percentage(run.eventsShortPercent)} short · limit ${settings ? percentage(settings.historicalTolerancePercent) : validationLimitLabel(history?.limitKind ?? "")}` },
      { label: "Sample traced", value: `${run.sampleTraced.toLocaleString()} of ${run.sampleTotal.toLocaleString()}`, detail: "stratified by item family and age" },
    ]} />}
    <section className="card migration-validation-card" aria-labelledby="validation-checks"><div className="card-header"><h2 id="validation-checks">Checks <span className="meta">source total, {targetLabel.toLowerCase()} total, difference</span></h2>{run?.rehearsal && <StatusBadge tone="info">Rehearsal</StatusBadge>}</div>
      <DataGrid columns={columns} page={{ rows: run?.checks ?? [], total: run?.checks.length ?? 0, page: 1, pageSize: run?.checks.length || 1, continuation: null, totalIsExact: true, hasMore: false }} rowId={check => check.checkId} onPage={() => undefined} onRowClick={check => void openCheck(check)} loading={loading} viewportHeight={620} emptyText={batchId ? "No validation run exists for this extraction." : "Choose an extraction."} footer={<span className="muted">{run ? `${run.checks.length.toLocaleString()} checks · select a row for counts, differences and signatures` : admin ? "Run the checks to create the first validation result." : "An administrator must start validation."}</span>} />
    </section>
  </MigrationPage><Rail label="Validation settings"><ValidationSettingsPanel settings={settings} admin={admin} onSaved={setSettings} /></Rail></>;
}

function CheckDrawer({ initial, initialDifferences, runId, roles, targetLabel, onChanged }: {
  initial: ValidationCheckDetail; initialDifferences: ValidationDifferencePage; runId: string; roles: readonly string[];
  targetLabel: string; onChanged: () => void | Promise<void>;
}) {
  const [detail, setDetail] = useState(initial);
  const [explanation, setExplanation] = useState(initial.check.explanation ?? "");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const check = detail.check;
  const prepare = mayPrepareValidation(roles);
  const review = mayReviewValidation(roles);

  const change = async (action: () => Promise<ValidationCheck>) => {
    setBusy(true); setError(null);
    try {
      await action();
      setDetail(await migrationApi.validationCheck(check.checkId));
      await onChanged();
    } catch (reason) { setError(reason); }
    finally { setBusy(false); }
  };

  return <>
    <Section title="Count"><Kv rows={[
      ["Limit", validationLimitLabel(check.limitKind)], ["Source", total(check.sourceTotal)],
      [targetLabel, total(check.targetTotal)], ["Difference", difference(check)],
      ["Ran", `${fmtDateTime(check.ranAt)} · ${duration(check.durationMilliseconds)}`],
      ["Result", <StatusBadge tone={validationResultTone(check.result)}>{validationResultLabel(check.result)}</StatusBadge>],
    ]} /><p className="muted migration-how-counted">{check.howCounted}</p>
      {detail.parts.length > 0 && <div className="migration-check-parts">{detail.parts.map(part => <span key={part.code}><span className="k">{part.label}</span><span className="v">{part.value.toLocaleString()}</span></span>)}</div>}
    </Section>
    {check.explanation && <Section title="Explanation"><p>{check.explanation}</p><p className="sub">{check.explainedByName ?? "Name unavailable"} · {fmtDateTime(check.explainedAt)}</p></Section>}
    <Section title="Signatures"><ol className="migration-signatures">{(["preparer", "reviewer"] as const).map((role, index) => {
      const signature = check.signatures.find(value => value.role === role);
      return <li key={role}><span className="migration-signature-step">{index + 1}</span><span><strong>{validationRoleLabel(role)}</strong><span className="sub">{signature ? `${signature.actorName} · ${fmtDateTime(signature.signedAt)}` : "Not signed"}</span></span><StatusBadge tone={signature ? "ok" : "neutral"}>{signature ? "Signed" : "Waiting"}</StatusBadge></li>;
    })}</ol>
      {detail.signatureHistory.some(signature => signature.superseded) && <div className="migration-signature-history"><h4>Superseded signatures</h4>{detail.signatureHistory.filter(signature => signature.superseded).map((signature, index) => <p key={`${signature.role}-${signature.signedAt}-${index}`}><s>{validationRoleLabel(signature.role)} · {signature.actorName} · {fmtDateTime(signature.signedAt)}</s><span className="sub"> Superseded {fmtDateTime(signature.supersededAt)}</span></p>)}</div>}
      {check.result !== "hard_fail" && <div className="migration-validation-actions">
        {prepare && !hasSignature(check, "preparer") && <button type="button" className="btn primary" disabled={busy} onClick={() => void change(() => migrationApi.signValidationCheck(check.checkId, "preparer"))}>Sign as preparer</button>}
        {review && !hasSignature(check, "reviewer") && <button type="button" className="btn" disabled={busy} onClick={() => void change(() => migrationApi.signValidationCheck(check.checkId, "reviewer"))}>Sign as reviewer</button>}
      </div>}
    </Section>
    {check.result === "fail" && <Section title="Explain this difference"><label className="field"><span className="lbl">Explanation (required)</span><textarea className="textarea" rows={4} maxLength={4000} required value={explanation} onChange={event => setExplanation(event.target.value)} /><span className="help">Accepts this difference for review; it does not turn a hard fail into an explanation.</span></label><button type="button" className="btn" disabled={busy || !explanation.trim()} onClick={() => void change(() => migrationApi.explainValidationCheck(check.checkId, explanation))}>Explain</button></Section>}
    <ValidationError error={error} />
    {check.code === "stratified_sample" ? <SampleTracing runId={runId} onChanged={async () => { setDetail(await migrationApi.validationCheck(check.checkId)); await onChanged(); }} /> : <DifferenceList checkId={check.checkId} initial={initialDifferences} />}
  </>;
}

function DifferenceList({ checkId, initial }: { checkId: string; initial: ValidationDifferencePage }) {
  const [page, setPage] = useState(initial);
  const [pageNumber, setPageNumber] = useState(1);
  const [cursors, setCursors] = useState<(string | undefined)[]>([undefined]);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  if (initial.items.length === 0 && page.items.length === 0) return null;
  const go = async (target: number) => {
    const cursor = target > pageNumber ? page.nextCursor ?? undefined : cursors[target - 1];
    if (target < 1 || (target > pageNumber && !cursor)) return;
    setBusy(true); setError(null);
    try {
      const next = await migrationApi.validationDifferences(checkId, cursor);
      setCursors(current => { const values = current.slice(); values[target - 1] = cursor; return values; });
      setPage(next); setPageNumber(target);
    } catch (reason) { setError(reason); }
    finally { setBusy(false); }
  };
  return <Section title="Differences"><div className="migration-differences">{page.items.map(item => <article key={item.differenceId}><strong>{validationDifferenceLabel(item.kind)}</strong><span className="sub">{[item.itemCode, item.warehouse, item.serial].filter(Boolean).join(" · ") || "Aggregate difference"}</span><Kv rows={[["Source", item.sourceValue.toLocaleString()], ["Target", item.targetValue.toLocaleString()], ["Difference", signedChange(item.difference)]]} />{item.detail && <p className="muted">{item.detail}</p>}</article>)}</div><ValidationError error={error} /><div className="migration-drawer-pager"><button type="button" className="btn sm" disabled={pageNumber === 1 || busy} onClick={() => void go(pageNumber - 1)}>Previous</button><span>Page {pageNumber}</span><button type="button" className="btn sm" disabled={!page.nextCursor || busy} onClick={() => void go(pageNumber + 1)}>Next</button></div></Section>;
}

function SampleTracing({ runId, onChanged }: { runId: string; onChanged: () => void | Promise<void> }) {
  const [page, setPage] = useState<ValidationSamplePage | null>(null);
  const [pageNumber, setPageNumber] = useState(1);
  const [cursors, setCursors] = useState<(string | undefined)[]>([undefined]);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const load = useCallback(async (target: number, cursor?: string) => {
    setBusy(true); setError(null);
    try { setPage(await migrationApi.validationSample(runId, cursor)); setPageNumber(target); }
    catch (reason) { setError(reason); }
    finally { setBusy(false); }
  }, [runId]);
  useEffect(() => { void load(1); }, [load]);
  const go = (target: number) => {
    const cursor = target > pageNumber ? page?.nextCursor ?? undefined : cursors[target - 1];
    if (target < 1 || (target > pageNumber && !cursor)) return;
    setCursors(current => { const values = current.slice(); values[target - 1] = cursor; return values; });
    void load(target, cursor);
  };
  const update = async (sample: ValidationSample) => {
    setPage(current => current ? { ...current, items: current.items.map(item => item.sampleId === sample.sampleId ? sample : item) } : current);
    await onChanged();
  };
  return <Section title="Sample tracing"><p className="muted">Compare the source trail beside the plan or module events. Every sampled serial needs an outcome.</p><ValidationError error={error} />{page?.items.map(sample => <SampleCard key={sample.sampleId} sample={sample} onUpdated={update} />)}{!busy && page?.items.length === 0 && <p className="muted">No sampled serials.</p>}<div className="migration-drawer-pager"><button type="button" className="btn sm" disabled={pageNumber === 1 || busy} onClick={() => go(pageNumber - 1)}>Previous</button><span>Page {pageNumber}</span><button type="button" className="btn sm" disabled={!page?.nextCursor || busy} onClick={() => go(pageNumber + 1)}>Next</button></div></Section>;
}

function SampleCard({ sample, onUpdated }: { sample: ValidationSample; onUpdated: (sample: ValidationSample) => void | Promise<void> }) {
  const [note, setNote] = useState(sample.note ?? "");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const trace = async (outcome: ValidationSampleOutcome) => {
    setBusy(true); setError(null);
    try { await onUpdated(await migrationApi.traceValidationSample(sample.sampleId, outcome, outcome === "mismatch" ? note.trim() : null)); }
    catch (reason) { setError(reason); }
    finally { setBusy(false); }
  };
  return <article className="migration-sample-card"><div className="migration-sample-head"><span><strong className="serial">{sample.serial}</strong><span className="sub">{sample.itemCode} · {sample.itemFamily} · {sample.ageBand}</span></span>{sample.outcome && <StatusBadge tone={sample.outcome === "traced" ? "ok" : "bad"}>{sample.outcome === "traced" ? "Traced" : "Mismatch"}</StatusBadge>}</div><div className="migration-trails"><Trail title="Source trail" json={sample.sourceTrailJson} /><Trail title="Plan or module events" json={sample.targetTrailJson} /></div>{sample.tracedByName && <p className="sub">{sample.tracedByName} · {fmtDateTime(sample.tracedAt)}{sample.note ? ` · ${sample.note}` : ""}</p>}<label className="field"><span className="lbl">Mismatch note</span><textarea className="textarea" rows={2} maxLength={2000} value={note} onChange={event => setNote(event.target.value)} /><span className="help">Required when the trails do not match.</span></label><ValidationError error={error} /><div className="migration-validation-actions"><button type="button" className="btn primary" disabled={busy} onClick={() => void trace("traced")}>Traced</button><button type="button" className="btn" disabled={busy || !note.trim()} onClick={() => void trace("mismatch")}>Mismatch</button></div></article>;
}

function Trail({ title, json }: { title: string; json: string }) {
  const events = validationTrail(json);
  return <div><h4>{title}</h4>{events.length ? <ol>{events.map((event, index) => <li key={index}>{validationTrailLine(event)}</li>)}</ol> : <p className="muted">No events recorded.</p>}</div>;
}

function ValidationSettingsPanel({ settings, admin, onSaved }: { settings: ValidationSettings | null; admin: boolean; onSaved: (settings: ValidationSettings) => void }) {
  const [tolerance, setTolerance] = useState("");
  const [sampleSize, setSampleSize] = useState("");
  const [bands, setBands] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [saved, setSaved] = useState(false);
  useEffect(() => {
    if (!settings) return;
    setTolerance(String(settings.historicalTolerancePercent)); setSampleSize(String(settings.sampleSize)); setBands(settings.ageBandYears.join(", "));
  }, [settings]);
  if (!settings) return <Section title="Tenant limits"><p className="muted">Loading settings…</p></Section>;
  if (!admin) return <Section title="Tenant limits"><Kv rows={[["History shortfall", percentage(settings.historicalTolerancePercent)], ["Sample", settings.sampleSize.toLocaleString()], ["Age bands", settings.ageBandYears.map(value => `${value} y`).join(", ")]]} /><p className="muted">A tenant administrator maintains these limits.</p></Section>;
  const bandParts = bands.split(",").map(value => value.trim()).filter(Boolean);
  const parsedBands = bandParts.map(Number);
  const bandsValid = parsedBands.length > 0 && parsedBands.every((value, index) => Number.isInteger(value) && value > 0 && (index === 0 || value > parsedBands[index - 1]!));
  const ready = Number(tolerance) >= 0 && Number(tolerance) <= 100 && Number(sampleSize) >= 100 && Number(sampleSize) <= 300 && bandsValid;
  const save = async () => {
    setBusy(true); setError(null); setSaved(false);
    try {
      const next = await migrationApi.saveValidationSettings(Number(tolerance), Number(sampleSize), parsedBands, settings.version);
      onSaved(next); setSaved(true);
    } catch (reason) { setError(reason); }
    finally { setBusy(false); }
  };
  return <Section title="Tenant limits"><div className="migration-settings-form"><label className="field"><span className="lbl">History shortfall (%)</span><input className="input num" type="number" min="0" max="100" step="0.01" value={tolerance} onChange={event => setTolerance(event.target.value)} /></label><label className="field"><span className="lbl">Sample size</span><input className="input num" type="number" min="100" max="300" value={sampleSize} onChange={event => setSampleSize(event.target.value)} /></label><label className="field"><span className="lbl">Age-band boundaries (years)</span><input className="input" value={bands} onChange={event => setBands(event.target.value)} /><span className="help">Positive, increasing years separated by commas.</span></label><ValidationError error={error} />{saved && <div className="note" role="status">Validation settings saved.</div>}<button type="button" className="btn primary" disabled={busy || !ready} onClick={() => void save()}>{busy ? "Saving…" : "Save settings"}</button></div></Section>;
}

function ValidationError({ error }: { error: unknown }) {
  if (!error) return null;
  const label = error instanceof ApiError ? refusalRuleLabel(error.rule) : "Something went wrong";
  const detail = error instanceof ApiError ? error.detail : error instanceof Error ? error.message : null;
  return <div className="form-error" role="alert"><strong>{label}.</strong>{detail && detail !== label ? ` ${detail}` : ""}</div>;
}

function total(value: number | null | undefined): string { return value === null || value === undefined ? "—" : value.toLocaleString(); }
function difference(check: Pick<ValidationCheck, "difference" | "code" | "sourceTotal"> | null | undefined): string {
  if (!check || check.difference === null) return "—";
  const value = signedChange(check.difference);
  if (check.code === "historical_events" && check.sourceTotal) return `${value} (${percentage(Math.abs(check.difference) * 100 / check.sourceTotal)})`;
  return value;
}
function hasSignature(check: ValidationCheck, role: ValidationSignatureRole): boolean { return check.signatures.some(signature => signature.role === role && !signature.superseded); }
function signatureNames(check: ValidationCheck): string { return (["preparer", "reviewer"] as const).map(role => check.signatures.find(signature => signature.role === role)?.actorName ?? "—").join(" · "); }
function signedDate(check: ValidationCheck): string { return check.signatures.length === 2 ? fmtDate(check.signatures.reduce((latest, signature) => signature.signedAt > latest ? signature.signedAt : latest, "")) : "—"; }
function hardFailFacts(checks: ValidationCheck[]): string {
  const facts = checks.filter(check => check.result === "hard_fail").map(check => `${check.sourceTotal?.toLocaleString() ?? "A"} ${validationCheckLabel(check.code, check.label).toLowerCase()}`);
  return facts.length ? `${facts.join(facts.length === 2 ? " and " : ", ")} in the source.` : "";
}

