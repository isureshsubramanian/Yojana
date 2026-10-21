import { useCallback, useEffect, useMemo, useState } from "react";
import { ApiError } from "../api/client";
import { DataGrid, KpiStrip, StatusBadge, type GridColumn } from "../components";
import { useSession } from "../session/session";
import { Rail } from "../shell/Drawer";
import { openDrawer, updateDrawer } from "../shell/drawerStore";
import { Section } from "../units/common";
import { fmtDate, fmtDateTime } from "../units/format";
import { date, useCursorPage } from "./AdminCommon";
import { MigrationAccess, MigrationPage } from "./MigrationCommon";
import { migrationApi } from "./migrationApi";
import {
  REVIEW_KINDS, REVIEW_STATUSES, batchLabel, candidateLetter, canonicalisationStatusLabel, matchRuleLabel,
  mayAdministerMigration, refusalRuleLabel, reviewDecisionLabel, reviewDecisionOptions, reviewKindLabel, reviewStatusLabel,
  serialAsStored, statusTone,
} from "./migrationModel";
import type {
  CanonicalisationRun, MigrationBatch, MigrationBatchPage, MigrationCandidateLineage, MigrationReviewDecision,
  MigrationReviewDetail, MigrationReviewKind, MigrationReviewPage, MigrationReviewRow, MigrationReviewStatus,
} from "./migrationTypes";

const TAKE = 25;

export function Component() { return <MigrationAccess><MigrationReview /></MigrationAccess>; }

function MigrationReview() {
  const admin = mayAdministerMigration(useSession().roles);
  const fetchBatches = useCallback((cursor?: string, signal?: AbortSignal) => migrationApi.batches(cursor, signal), []);
  const batches = useCursorPage<MigrationBatch, MigrationBatchPage>(fetchBatches);
  const [runs, setRuns] = useState<Record<string, CanonicalisationRun | null>>({});
  const [batchId, setBatchId] = useState("");
  const [kind, setKind] = useState<MigrationReviewKind | "">("");
  const [family, setFamily] = useState("");
  const [status, setStatus] = useState<MigrationReviewStatus | "">("waiting");
  const [families, setFamilies] = useState<string[]>([]);
  const [pageNumber, setPageNumber] = useState(1);
  const [cursors, setCursors] = useState<(string | undefined)[]>([undefined]);
  const [page, setPage] = useState<MigrationReviewPage | null>(null);
  const [active, setActive] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  const [runBusy, setRunBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [runError, setRunError] = useState<unknown>(null);
  const [notice, setNotice] = useState("");

  useEffect(() => {
    const available = batches.result?.items;
    if (!available?.length) return;
    const controller = new AbortController();
    Promise.all(available.map(async batch => {
      try { return [batch.batchId, await migrationApi.canonicalisation(batch.batchId, controller.signal)] as const; }
      catch (reason) {
        if (reason instanceof ApiError && reason.status === 404) return [batch.batchId, null] as const;
        throw reason;
      }
    })).then(results => {
      if (controller.signal.aborted) return;
      const next = Object.fromEntries(results);
      setRuns(current => ({ ...current, ...next }));
      setBatchId(current => available.some(batch => batch.batchId === current) ? current
        : available.find(batch => next[batch.batchId]?.status === "completed")?.batchId ?? available[0]!.batchId);
    }).catch(reason => { if (!controller.signal.aborted) setRunError(reason); });
    return () => controller.abort();
  }, [batches.result]);

  const currentBatch = batches.result?.items.find(batch => batch.batchId === batchId) ?? null;
  const run = !batchId || !Object.prototype.hasOwnProperty.call(runs, batchId) ? undefined : runs[batchId] ?? null;
  const canList = run?.status === "completed";

  const loadPage = useCallback(async (target: number, cursor?: string, signal?: AbortSignal) => {
    if (!batchId || !canList) { setPage(null); setLoading(false); return; }
    setLoading(true); setError(null);
    try {
      const next = await migrationApi.review({ batchId, kind, family: family.trim(), status, cursor, take: TAKE }, signal);
      setPage(next); setPageNumber(target);
      setFamilies(current => [...new Set([...current, ...next.items.map(row => row.family).filter((value): value is string => !!value)])].sort((a, b) => a.localeCompare(b)));
    } catch (reason) {
      if (!(reason instanceof DOMException && reason.name === "AbortError")) setError(reason);
    } finally { if (!signal?.aborted) setLoading(false); }
  }, [batchId, canList, family, kind, status]);

  useEffect(() => {
    const controller = new AbortController();
    setCursors([undefined]); setPageNumber(1); setActive(null);
    void loadPage(1, undefined, controller.signal);
    return () => controller.abort();
  }, [loadPage]);

  const refresh = useCallback(async () => {
    if (!batchId) return;
    const [nextRun] = await Promise.all([
      migrationApi.canonicalisation(batchId),
      loadPage(pageNumber, cursors[pageNumber - 1]),
    ]);
    setRuns(current => ({ ...current, [batchId]: nextRun }));
  }, [batchId, cursors, loadPage, pageNumber]);

  const rows = page?.items ?? [];
  const openItem = useCallback(async (row: MigrationReviewRow) => {
    setActive(row.itemId);
    const title = serialAsStored(row.serialAsStored);
    openDrawer({ eyebrow: "Review decision", title, mono: true }, <p className="muted">Loading candidates…</p>);
    try {
      const detail = await migrationApi.reviewDetail(row.itemId);
      const skip = () => {
        const index = rows.findIndex(item => item.itemId === row.itemId);
        const next = rows.slice(index + 1).find(item => item.status === "waiting") ?? rows.find(item => item.status === "waiting" && item.itemId !== row.itemId);
        if (next) void openItem(next);
      };
      updateDrawer({ eyebrow: "Review decision", title, mono: true }, <ReviewDrawer detail={detail} onSaved={refresh} onSkip={skip} hasNext={rows.some(item => item.status === "waiting" && item.itemId !== row.itemId)} />);
    } catch (reason) {
      updateDrawer({ eyebrow: "Review decision", title, mono: true }, <ReviewError error={reason} />);
    }
  }, [refresh, rows]);

  const columns = useMemo<GridColumn<MigrationReviewRow>[]>(() => [
    { key: "serial", label: "Serial as stored", width: 23, render: row => <span className="primary"><span className="t serial">{serialAsStored(row.serialAsStored)}</span><span className="sub">{[row.itemName, row.itemCode].filter(Boolean).join(" · ") || "Item unavailable"}</span></span> },
    { key: "kind", label: "Kind", width: 18, render: row => reviewKindLabel(row.kind) },
    { key: "rule", label: "Rule tried", width: 16, render: row => matchRuleLabel(row.ruleTried) },
    { key: "candidates", label: "Candidates", width: 27, render: row => <span title={row.hint ?? row.suggestionReason ?? undefined}>{row.candidateSummary}</span> },
    { key: "suggestion", label: "Suggested", width: 16, render: row => <Suggestion row={row} /> },
  ], []);

  const go = (target: number) => {
    if (target < 1 || target === pageNumber) return;
    const cursor = target > pageNumber ? page?.nextCursor ?? undefined : cursors[target - 1];
    if (target > pageNumber && !cursor) return;
    setCursors(current => { const next = current.slice(); next[target - 1] = cursor; return next; });
    void loadPage(target, cursor);
  };

  const start = async () => {
    if (!batchId) return;
    setRunBusy(true); setRunError(null); setNotice("");
    try {
      const queued = await migrationApi.runCanonicalisation(batchId);
      setRuns(current => ({ ...current, [batchId]: emptyRun(queued.runId, queued.batchId, queued.status) }));
      setNotice("Canonicalisation queued.");
    } catch (reason) { setRunError(reason); }
    finally { setRunBusy(false); }
  };

  const gaps = run?.gaps.filter(gap => gap.kind === "serial_in_user_field") ?? [];
  const heading = status === "waiting" ? "Waiting for a decision" : status === "decided" ? "Decided review items" : status === "carried_over" ? "Carried-over decisions" : "All review items";
  return <><MigrationPage title="Review queue" eyebrow={currentBatch ? <>{batchLabel(currentBatch, batches.grid.page === 1 && batches.result?.items[0]?.batchId === currentBatch.batchId)} · canonicalisation · decisions carry over to later extractions</> : "Canonicalisation decisions"} sub="Source rows that the match rules could not settle. Nothing here is merged until a person decides, and nothing is ever merged across two items." waitingReviews={run?.waitingForPerson}>
    <div className="toolbar migration-review-toolbar"><label className="field"><span className="lbl">Extraction</span><select className="select" value={batchId} onChange={event => setBatchId(event.target.value)}><option value="">Choose an extraction</option>{batches.result?.items.map((batch, index) => <option value={batch.batchId} key={batch.batchId}>{batchLabel(batch, batches.grid.page === 1 && index === 0)} · {date(batch.requestedAt)}</option>)}</select></label><button type="button" className="btn sm" disabled={batches.grid.page === 1 || batches.busy} onClick={() => batches.onPage(batches.grid.page - 1)}>Newer extractions</button><button type="button" className="btn sm" disabled={!batches.grid.hasMore || batches.busy} onClick={() => batches.onPage(batches.grid.page + 1)}>Older extractions</button></div>
    <ReviewError error={batches.error ?? runError ?? error} />{notice && <div className="note" role="status">{notice}</div>}
    {run !== undefined && run?.status !== "completed" && <section className="card migration-canonical-state" aria-labelledby="canonical-state"><div className="card-header"><h2 id="canonical-state">Canonicalisation <span className="meta">{canonicalisationStatusLabel(run?.status ?? "not_run")}</span></h2>{run && <StatusBadge tone={statusTone(run.status)}>{canonicalisationStatusLabel(run.status)}</StatusBadge>}</div><div className="card-body"><p className="muted">This extraction has no completed canonicalisation run, so its review queue is not available yet.</p>{run?.failure && <ReviewError error={new Error(refusalRuleLabel(run.failure))} />}{admin && <button type="button" className="btn primary" disabled={runBusy || /^(queued|running)$/.test(run?.status ?? "")} onClick={() => void start()}>{runBusy ? "Queuing…" : "Run canonicalisation"}</button>}</div></section>}
    {run?.status === "completed" && <KpiStrip label="Canonicalisation review figures" kpis={[
      { label: "Waiting", value: run.waitingForPerson.toLocaleString() },
      { label: "Merged", value: run.merged.toLocaleString(), detail: "candidate rows merged" },
      { label: "Kept apart", value: run.keptApart.toLocaleString(), detail: "different units, same serial text" },
      { label: "Fix in the incumbent", value: run.fixInIncumbent.toLocaleString(), detail: "sent back with a note" },
      { label: "Carried over from earlier extractions", value: run.carriedOver.toLocaleString(), detail: "matching lineage and source facts" },
    ]} />}
    {gaps.map(gap => <div className="note warn" role="note" key={gap.kind}><strong>{reviewKindLabel(gap.kind)} is not available:</strong>&nbsp;{gap.reason}</div>)}
    <section className="card migration-review-card" aria-labelledby="review-items"><div className="card-header"><h2 id="review-items">{heading} <span className="meta">oldest first</span></h2>{run && <StatusBadge tone={statusTone(run.status)}>{canonicalisationStatusLabel(run.status)}</StatusBadge>}</div>
      <div className="toolbar flat"><select className="select" aria-label="Kind" value={kind} onChange={event => setKind(event.target.value as MigrationReviewKind | "")}><option value="">Any kind</option>{REVIEW_KINDS.map(value => <option value={value} key={value}>{reviewKindLabel(value)}</option>)}</select><label className="migration-family-filter"><span className="sr-only">Item family</span><input className="input" list="migration-review-families" value={family} onChange={event => setFamily(event.target.value)} placeholder="Any item family" aria-label="Item family" /></label><datalist id="migration-review-families">{families.map(value => <option value={value} key={value} />)}</datalist><select className="select" aria-label="Status" value={status} onChange={event => setStatus(event.target.value as MigrationReviewStatus | "")}><option value="">All statuses</option>{REVIEW_STATUSES.map(value => <option value={value} key={value}>{reviewStatusLabel(value)}</option>)}</select></div>
      <DataGrid columns={columns} page={{ rows, total: rows.length, page: pageNumber, pageSize: TAKE, continuation: page?.nextCursor ?? null, totalIsExact: false, hasMore: !!page?.nextCursor }} rowId={row => row.itemId} onPage={go} onRowClick={row => void openItem(row)} activeRowId={active} loading={loading} viewportHeight={540} emptyText={canList ? "No review items match these filters." : "Choose an extraction with completed canonicalisation."} footer={<><span className="muted">{rows.length.toLocaleString()} on this page · {TAKE} a page · keyset paged</span><span className="grow" /><div className="pager"><button type="button" className="btn sm" disabled={pageNumber === 1 || loading} onClick={() => go(pageNumber - 1)}>Previous</button><span className="pg">Page {pageNumber}</span><button type="button" className="btn sm" disabled={!page?.nextCursor || loading} onClick={() => go(pageNumber + 1)}>Next</button></div></>} />
    </section>
  </MigrationPage><Rail label="Review queue"><Section title="Final decisions"><p className="muted">Every decision keeps its reason and reviewer. A later extraction carries it over only while the candidate lineage and source facts still match.</p></Section></Rail></>;
}

function Suggestion({ row }: { row: MigrationReviewRow }) {
  if (row.status !== "waiting") return <StatusBadge tone={row.status === "carried_over" ? "ok" : "neutral"}>{reviewStatusLabel(row.status)}</StatusBadge>;
  if (row.suggestion === "merge_into") return <StatusBadge tone="info">Merge suggested</StatusBadge>;
  if (row.suggestion === "keep_apart") return <StatusBadge tone="neutral">Keep apart</StatusBadge>;
  if (row.suggestion === "fix_in_incumbent") return <StatusBadge tone="warn">Fix in the incumbent</StatusBadge>;
  return <StatusBadge tone={row.hint ? "warn" : "neutral"}>{row.hint ? "Review hint" : "No suggestion"}</StatusBadge>;
}

export function ReviewDrawer({ detail, onSaved, onSkip, hasNext }: { detail: MigrationReviewDetail; onSaved: () => void | Promise<void>; onSkip: () => void; hasNext: boolean }) {
  const options = reviewDecisionOptions(detail);
  const suggested = options.find(option => option.suggested) ?? null;
  const [decision, setDecision] = useState<MigrationReviewDecision | "">(suggested?.decision ?? "");
  const [keepSourceKey, setKeepSourceKey] = useState<string | null>(suggested?.keepSourceKey ?? null);
  const [reason, setReason] = useState("");
  const [note, setNote] = useState("");
  const [busy, setBusy] = useState(false);
  const [saved, setSaved] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const ready = !!decision && !!reason.trim() && (decision !== "fix_in_incumbent" || !!note.trim());
  const editable = detail.status === "waiting" && !saved;
  const select = (next: typeof options[number]) => { setDecision(next.decision); setKeepSourceKey(next.keepSourceKey); setSaved(false); };
  const save = async () => {
    if (!ready || !decision) return;
    setBusy(true); setError(null); setSaved(false);
    try {
      await migrationApi.decideReview(detail.itemId, decision, keepSourceKey, reason.trim(), note);
      setSaved(true); await onSaved();
    } catch (failure) { setError(failure); }
    finally { setBusy(false); }
  };
  return <>
    <div className="section"><div className="migration-review-candidates">{detail.candidates.map((candidate, index) => <CandidateCard candidate={candidate} index={index} suggested={candidate.sourceKey === detail.suggestedKeepSourceKey} key={candidate.sourceKey} />)}</div>{!detail.candidates.length && <p className="muted">No same-item candidate is available.</p>}</div>
    <div className="section"><div className="eyebrow">Why</div><p className="muted migration-review-why">{detail.suggestionReason ?? detail.hint ?? "The match rules did not produce a suggestion; review the source facts before deciding."}</p></div>
    {editable ? <div className="section"><fieldset className="migration-review-decisions"><legend>Decision</legend><div className="migration-review-options">{options.map(option => <label className={decision === option.decision && keepSourceKey === option.keepSourceKey ? "on" : ""} key={`${option.decision}-${option.keepSourceKey ?? "none"}`}><input type="radio" name={`review-${detail.itemId}`} checked={decision === option.decision && keepSourceKey === option.keepSourceKey} onChange={() => select(option)} /><span><strong>{option.label}</strong>{option.suggested && <span className="migration-suggested">Suggested</span>}<span className="help">{option.help}</span></span></label>)}</div></fieldset>
      <label className="field"><span className="lbl">Reason (kept with the decision)</span><textarea className="textarea" rows={3} maxLength={2000} required value={reason} onChange={event => setReason(event.target.value)} /></label>
      {decision === "fix_in_incumbent" && <label className="field"><span className="lbl">Note for the source correction</span><textarea className="textarea" rows={3} maxLength={2000} required value={note} onChange={event => setNote(event.target.value)} /><span className="help">Required when the issue will be fixed in the incumbent.</span></label>}
      <ReviewError error={error} /><div className="migration-review-actions"><button type="button" className="btn primary" disabled={busy || !ready} onClick={() => void save()}>{busy ? "Saving…" : "Save decision"}</button><button type="button" className="btn" disabled={!hasNext} onClick={onSkip}>Skip</button></div>
    </div> : <div className="section"><StatusBadge tone={detail.status === "carried_over" || saved ? "ok" : "neutral"}>{saved ? "Decided" : reviewStatusLabel(detail.status)}</StatusBadge><p className="muted">{saved ? "Decision saved. This item now has a final decision." : "This item is read-only because it already has a final decision."}</p>{saved && hasNext && <button type="button" className="btn" onClick={onSkip}>Next waiting item</button>}</div>}
    <EarlierDecisions detail={detail} />
  </>;
}

function CandidateCard({ candidate, index, suggested }: { candidate: MigrationCandidateLineage; index: number; suggested: boolean }) {
  const facts = [
    `${candidate.transactions.toLocaleString()} ${candidate.transactions === 1 ? "transaction" : "transactions"}`,
    candidate.firstReceipt ? `first receipt ${fmtDate(candidate.firstReceipt)}` : "no receipt recorded",
    candidate.lastDelivery ? `last delivery ${fmtDate(candidate.lastDelivery)}${candidate.lastDeliveryParty ? ` to ${candidate.lastDeliveryParty}` : ""}` : "no delivery recorded",
    candidate.hasEquipmentCard ? "equipment card" : "no equipment card",
    candidate.hasContract ? "contract" : "no contract",
  ];
  return <article className={`migration-candidate${suggested ? " suggested" : ""}`}><div className="k">{candidateLetter(index)}{suggested ? " · suggested" : ""}</div><div className="serial">{serialAsStored(candidate.serialAsStored)}</div><div className="primary">{candidate.itemName ?? candidate.itemCode}<span className="sub">{candidate.itemName ? candidate.itemCode : "Item name unavailable"}</span></div><p className="muted">{facts.join(" · ")}</p></article>;
}

function EarlierDecisions({ detail }: { detail: MigrationReviewDetail }) {
  if (!detail.earlierDecisions.length) return null;
  return <div className="section"><h3>Earlier decisions</h3><div className="migration-earlier-decisions">{detail.earlierDecisions.map((decision, index) => {
    const state = decision.appliedToThisBatch ? "Applied to this extraction" : detail.status === "carried_over" && index === 0 ? "Carried over to this extraction" : "Earlier extraction · not applied";
    return <article key={decision.decisionId}><div><StatusBadge tone={decision.appliedToThisBatch || state.startsWith("Carried") ? "ok" : "neutral"}>{state}</StatusBadge></div><strong>{reviewDecisionLabel(decision.decision)}</strong><span className="sub">{decision.decidedByName} · {fmtDateTime(decision.decidedAt)}</span><p>{decision.reason}</p>{decision.note && <p className="muted">Note: {decision.note}</p>}</article>;
  })}</div></div>;
}

function ReviewError({ error }: { error: unknown }) {
  if (!error) return null;
  const label = error instanceof ApiError ? refusalRuleLabel(error.rule) : "Something went wrong";
  const detail = error instanceof ApiError ? error.detail : error instanceof Error ? error.message : null;
  return <div className="form-error" role="alert"><strong>{label}.</strong>{detail && detail !== label ? ` ${detail}` : ""}</div>;
}

function emptyRun(runId: string, batchId: string, status: string): CanonicalisationRun {
  return { runId, batchId, status, startedAt: null, finishedAt: null, failure: null, serialsInSource: 0, exact: 0, trimmedUpper: 0, familyNormalised: 0, waitingForPerson: 0, merged: 0, keptApart: 0, fixInIncumbent: 0, carriedOver: 0, unverifiedDates: 0, gaps: [] };
}
