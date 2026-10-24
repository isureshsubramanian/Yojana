import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { Link, useSearchParams } from "react-router";
import { DataGrid, KpiStrip, StatusBadge, type GridColumn } from "../components";
import { useReferenceNames } from "../corrections/ReferenceFields";
import { usePeople } from "../people/people";
import { Rail } from "../shell/Drawer";
import { openDrawer } from "../shell/drawerStore";
import { Breadcrumbs } from "../shell/PageHeader";
import { ErrorNote, Kv, Section, UnitLink } from "../units/common";
import { fmtDate, fmtDateTime } from "../units/format";
import { claimsApi, type ClaimRiskFilters } from "./api";
import { ClaimRiskFlagChip } from "./ClaimRiskUi";
import { ClaimsTabs } from "./ClaimsTabs";
import {
  claimRiskDecisionLabel, claimRiskEvidence, claimRiskRuleLabel, claimRiskThresholdLabel, humanise, money,
} from "./format";
import {
  CLAIM_RISK_RULE_CODES, type ClaimDetail, type ClaimRiskDecision, type ClaimRiskFlagReportRow,
  type ClaimRiskFlagRow, type ClaimRiskReviewPage, type ClaimRiskReviewRow, type ClaimRiskReviewSummary,
  type ClaimRiskRulesPolicy,
} from "./types";
import "./claims.css";

const TAKE = 25;
type Period = "30" | "90";
type Draft = { decision: ClaimRiskDecision | ""; reason: string };

export function Component() {
  const [search] = useSearchParams();
  const claimParam = search.get("claim");
  const openedClaim = useRef<string | null>(null);
  const [status, setStatus] = useState<ClaimRiskFilters["status"]>("open");
  const [rule, setRule] = useState("");
  const [period, setPeriod] = useState<Period>("90");
  const [pageNumber, setPageNumber] = useState(1);
  const [cursors, setCursors] = useState<(string | undefined)[]>([undefined]);
  const [page, setPage] = useState<ClaimRiskReviewPage | null>(null);
  const [summary, setSummary] = useState<ClaimRiskReviewSummary | null>(null);
  const [report, setReport] = useState<ClaimRiskFlagReportRow[] | null>(null);
  const [policy, setPolicy] = useState<ClaimRiskRulesPolicy | null>(null);
  const [active, setActive] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<unknown>(null);
  const range = useMemo(() => periodRange(period), [period]);

  const loadPage = useCallback(async (target: number, cursor?: string, signal?: AbortSignal) => {
    setLoading(true); setError(null);
    try {
      const next = await claimsApi.riskReview({ status, rule, from: range.from, to: range.to, cursor, take: TAKE }, signal);
      setPage(next); setPageNumber(target);
    } catch (reason) {
      if (!(reason instanceof DOMException && reason.name === "AbortError")) setError(reason);
    } finally { setLoading(false); }
  }, [range.from, range.to, rule, status]);

  useEffect(() => {
    const controller = new AbortController();
    setCursors([undefined]); setPageNumber(1);
    void loadPage(1, undefined, controller.signal);
    return () => controller.abort();
  }, [loadPage]);

  const loadFigures = useCallback(async (signal?: AbortSignal) => {
    try {
      const [nextSummary, nextReport] = await Promise.all([
        claimsApi.riskReviewSummary(range.from, range.to, signal),
        claimsApi.riskReport(range.from, range.to, signal),
      ]);
      setSummary(nextSummary); setReport(nextReport);
    } catch (reason) {
      if (!(reason instanceof DOMException && reason.name === "AbortError")) setError(reason);
    }
  }, [range.from, range.to]);

  useEffect(() => {
    const controller = new AbortController();
    void loadFigures(controller.signal);
    return () => controller.abort();
  }, [loadFigures]);

  useEffect(() => {
    const controller = new AbortController();
    claimsApi.riskRules(controller.signal).then(setPolicy)
      .catch((reason) => { if (!(reason instanceof DOMException && reason.name === "AbortError")) setError(reason); });
    return () => controller.abort();
  }, []);

  const refresh = useCallback(async () => {
    await Promise.all([loadPage(pageNumber, cursors[pageNumber - 1]), loadFigures()]);
  }, [cursors, loadFigures, loadPage, pageNumber]);
  const openClaim = useCallback(async (claimId: string, knownNumber?: string) => {
    setActive(claimId);
    const number = knownNumber ?? "Claim";
    openDrawer({ eyebrow: "Risk review", title: number, mono: true }, <p className="muted">Loading risk flags…</p>);
    try {
      const [detail, flags] = await Promise.all([
        claimsApi.detail(claimId), claimsApi.claimRiskFlags(claimId),
      ]);
      openDrawer({ eyebrow: "Risk review", title: detail.claimNumber, mono: true }, <RiskReviewDrawer detail={detail} initialFlags={flags} onSaved={refresh} />);
    } catch (reason) {
      openDrawer({ eyebrow: "Risk review", title: number, mono: true }, <ErrorNote error={reason} />);
    }
  }, [refresh]);

  useEffect(() => {
    if (!claimParam || openedClaim.current === claimParam) return;
    openedClaim.current = claimParam;
    void openClaim(claimParam);
  }, [claimParam, openClaim]);

  const rows = page?.items ?? [];
  const names = useReferenceNames({ parties: rows.map((row) => row.claimantPartyRef), items: rows.map((row) => row.itemId) });
  const columns = useMemo<GridColumn<ClaimRiskReviewRow>[]>(() => [
    { key: "claim", label: "Claim", width: 16, render: (row) => <span className="primary"><span className="t serial">{row.claimNumber}</span><span className="sub">{humanise(row.remedy)} · {money(row.requestedAmount, row.currency)}</span></span> },
    { key: "unit", label: "Unit", width: 20, render: (row) => <><UnitLink unitId={row.unitId}>{row.serial ?? "No serial recorded"}</UnitLink><span className="sub">{names.item(row.itemId)}</span></> },
    { key: "claimant", label: "Claimant", width: 20, render: (row) => names.party(row.claimantPartyRef) },
    { key: "flags", label: "Flags", width: 32, render: (row) => <div className="claim-risk-chips">{row.flags.map((flag) => <ClaimRiskFlagChip flag={flag} key={flag.flagId} />)}</div> },
    { key: "flagged", label: "Flagged", width: 12, render: (row) => fmtDateTime(row.firstRaisedAt) },
  ], [names]);

  const go = (target: number) => {
    if (target < 1 || target === pageNumber) return;
    const cursor = target > pageNumber ? page?.nextCursor ?? undefined : cursors[target - 1];
    if (target > pageNumber && !cursor) return;
    setCursors((current) => { const next = current.slice(); next[target - 1] = cursor; return next; });
    void loadPage(target, cursor);
  };
  const reportRows = report ?? [];

  return <><main className="content" id="main" tabIndex={-1}>
    <Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Claims", to: "/claims" }, { label: "Risk review" }]} />
    <div className="page-header"><div><div className="eyebrow">Claims · rules only, no scoring model · a flag holds approval, it never denies</div><h1>Risk review</h1><p className="sub">Claims that a rule flagged for a second look before approval. Clear each flag or confirm the concern; the adjudicator still decides the claim.</p></div><div className="actions"><Link className="btn" to="/settings/claim-risk" state={{ from: "risk-review" }}>Rule settings</Link></div></div>
    <ClaimsTabs current="risk" riskCount={summary?.heldClaims} />
    <ErrorNote error={error} />
    <div className="stack" style={{ marginTop: error ? 16 : 0 }}>
      <KpiStrip label="Risk review figures" kpis={[
        { label: "Held for review", value: summary?.heldClaims.toLocaleString() ?? "—", detail: summary ? `${summary.openFlags.toLocaleString()} open flags · ${oldestText(summary.oldestOpenFlagRaisedAt)}` : "Loading summary…" },
        { label: `Flagged, last ${period} days`, value: summary ? percentage(summary.claimsFlaggedRate) : "—", detail: summary ? `${summary.claimsFlagged.toLocaleString()} of ${summary.claimsSubmitted.toLocaleString()} claims · ${summary.flagsRaised.toLocaleString()} flags` : "Loading summary…" },
        { label: "Concern confirmed", value: summary ? percentage(summary.confirmationRate) : "—", detail: summary ? `${summary.flagsConfirmed.toLocaleString()} of ${summary.flagsRaised.toLocaleString()} flags · ${summary.claimsDeniedAfterConfirmedFlag.toLocaleString()} claims then denied` : "Loading summary…" },
        { label: "Typical wait", value: hoursText(summary?.medianHoursToDecision), detail: "from flag to decision" },
      ]} />
      <section className="card claim-risk-queue" aria-labelledby="risk-queue"><div className="card-header"><h2 id="risk-queue">{status === "open" ? "Held claims" : status === "decided" ? "Decided claims" : "Risk-flagged claims"} <span className="meta">{status === "open" && !rule ? `${summary?.heldClaims.toLocaleString() ?? "—"} · ` : ""}oldest first</span></h2></div>
        <div className="card-body"><div className="toolbar">
          <label className="chip-field"><span className="chip-k">State</span><select className="chip-select on" aria-label="State" value={status} onChange={(event) => setStatus(event.target.value as ClaimRiskFilters["status"])}><option value="open">Open flags</option><option value="decided">Decided</option><option value="all">All</option></select></label>
          <label className="chip-field"><span className="chip-k">Rule</span><select className={`chip-select${rule ? " on" : ""}`} aria-label="Rule" value={rule} onChange={(event) => setRule(event.target.value)}><option value="">Any rule</option>{CLAIM_RISK_RULE_CODES.map((code) => <option value={code} key={code}>{claimRiskRuleLabel(code)}</option>)}</select></label>
          <label className="chip-field"><span className="chip-k">Period</span><select className="chip-select on" aria-label="Period" value={period} onChange={(event) => setPeriod(event.target.value as Period)}><option value="30">Last 30 days</option><option value="90">Last 90 days</option></select></label>
        </div></div>
        <DataGrid columns={columns} page={{ rows, total: rows.length, page: pageNumber, pageSize: Math.max(1, rows.length), continuation: page?.nextCursor ?? null, totalIsExact: false, hasMore: page?.hasMore ?? false }} rowId={(row) => row.claimId} onPage={go} onRowClick={(row) => void openClaim(row.claimId, row.claimNumber)} activeRowId={active} loading={loading} viewportHeight={520} emptyText="No claims have flags matching this view." footer={<><span>{rows.length.toLocaleString()} {rows.length === 1 ? "claim" : "claims"} on this page · keyset paged by claim</span><span className="grow" /><div className="pager"><button type="button" className="btn sm" disabled={pageNumber === 1 || loading} onClick={() => go(pageNumber - 1)}>Previous</button><span className="pg">Page {pageNumber}</span><button type="button" className="btn sm" disabled={!page?.hasMore || loading} onClick={() => go(pageNumber + 1)}>Next</button></div></>} />
      </section>
      <section className="card claim-risk-report" aria-labelledby="risk-report"><div className="card-header"><h2 id="risk-report">How the rules are doing <span className="meta">last {period} days · tune a threshold when a rule is mostly cleared</span></h2><Link className="btn sm" to="/settings/claim-risk" state={{ from: "risk-review" }}>Edit rules</Link></div><div className="grid-viewport"><table className="table"><colgroup><col style={{ width: "25%" }} /><col style={{ width: "35%" }} /><col style={{ width: "8%" }} /><col style={{ width: "8%" }} /><col style={{ width: "8%" }} /><col style={{ width: "9%" }} /><col style={{ width: "7%" }} /></colgroup><thead><tr><th>Rule</th><th>Current threshold</th><th className="num">Flags</th><th className="num">Cleared</th><th className="num">Confirmed</th><th className="num">Confirmed %</th><th className="num">Then denied<span className="sub">claims</span></th></tr></thead><tbody>{reportRows.map((row) => <tr key={row.ruleCode}><td className="primary">{claimRiskRuleLabel(row.ruleCode)}</td><td>{claimRiskThresholdLabel(row.ruleCode, policy)}</td><td className="num">{row.hits.toLocaleString()}</td><td className="num">{row.cleared.toLocaleString()}</td><td className="num">{row.confirmed.toLocaleString()}</td><td className="num">{percentage(row.confirmationRate)}</td><td className="num">{row.distinctClaimsDeniedAfterConfirmedFlag.toLocaleString()}</td></tr>)}</tbody></table></div></section>
    </div>
  </main><Rail label="Risk review"><Section title="Second review"><p className="muted">A rule flag holds approval until it has a final decision. Confirming a concern records the review; it does not deny the claim.</p></Section></Rail></>;
}

function RiskReviewDrawer({ detail, initialFlags, onSaved }: { detail: ClaimDetail; initialFlags: ClaimRiskFlagRow[]; onSaved: () => void | Promise<void> }) {
  const [flags, setFlags] = useState(initialFlags);
  const [drafts, setDrafts] = useState<Record<string, Draft>>(() => Object.fromEntries(initialFlags.filter((flag) => flag.status === "open").map((flag) => [flag.flagId, { decision: "", reason: "" }])));
  const [busy, setBusy] = useState(false);
  const [saved, setSaved] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const names = useReferenceNames({ parties: [detail.claimantPartyRef], items: [detail.itemId] });
  const person = usePeople([detail.submittedBy]);
  const open = flags.filter((flag) => flag.status === "open");
  const ready = open.length > 0 && open.every((flag) => drafts[flag.flagId]?.decision && drafts[flag.flagId]?.reason.trim());
  const update = (flagId: string, patch: Partial<Draft>) => setDrafts((current) => ({ ...current, [flagId]: { decision: current[flagId]?.decision ?? "", reason: current[flagId]?.reason ?? "", ...patch } }));
  const save = async () => {
    if (!ready) return;
    setBusy(true); setSaved(false); setError(null);
    try {
      for (const flag of open) {
        const draft = drafts[flag.flagId]!;
        await claimsApi.decideRiskFlag(detail.claimId, flag.flagId, draft.decision as ClaimRiskDecision, draft.reason.trim());
      }
      setFlags(await claimsApi.claimRiskFlags(detail.claimId));
      setSaved(true); await onSaved();
    } catch (reason) {
      setError(reason);
      try { setFlags(await claimsApi.claimRiskFlags(detail.claimId)); } catch { /* keep the refusal visible */ }
      await onSaved();
    } finally { setBusy(false); }
  };

  return <>
    <div className="section"><Kv rows={[
      ["Unit", <><span className="serial">{detail.serial ?? "No serial recorded"}</span> · {names.item(detail.itemId)}</>],
      ["Claimant", names.party(detail.claimantPartyRef)], ["Submitted by", `${person(detail.submittedBy)} · ${fmtDateTime(detail.submittedAt)}`],
      ["Claim date", fmtDate(detail.claimDate)], ["Remedy", humanise(detail.remedy)], ["Status", humanise(detail.status)],
    ]} /></div>
    <ErrorNote error={error} />
    {saved && <div className="note ok" role="status"><strong>Risk decisions saved.</strong>&nbsp;They are final and cannot be changed.</div>}
    {flags.map((flag) => <div className="section claim-risk-flag" key={flag.flagId}><div className="eyebrow">{claimRiskRuleLabel(flag.ruleCode)}</div>
      <div className="claim-risk-evidence">{claimRiskEvidence(flag.ruleCode, flag.evidence, flag.thresholds).map((sentence) => <p key={sentence}>{sentence}</p>)}</div>
      {flag.status === "open" ? <fieldset className="claim-risk-decision"><legend>Decision for {claimRiskRuleLabel(flag.ruleCode)}</legend><div className="claim-risk-options">
        <label className={drafts[flag.flagId]?.decision === "cleared" ? "on" : ""}><input type="radio" name={`decision-${flag.flagId}`} checked={drafts[flag.flagId]?.decision === "cleared"} onChange={() => update(flag.flagId, { decision: "cleared" })} /><span><strong>Clear</strong><span className="help">Not a concern</span></span></label>
        <label className={drafts[flag.flagId]?.decision === "confirmed" ? "on" : ""}><input type="radio" name={`decision-${flag.flagId}`} checked={drafts[flag.flagId]?.decision === "confirmed"} onChange={() => update(flag.flagId, { decision: "confirmed" })} /><span><strong>Confirm concern</strong><span className="help">The adjudicator still decides the claim</span></span></label>
      </div><div className="field"><label htmlFor={`reason-${flag.flagId}`}>Reason</label><textarea id={`reason-${flag.flagId}`} className="textarea" rows={3} maxLength={1000} required value={drafts[flag.flagId]?.reason ?? ""} onChange={(event) => update(flag.flagId, { reason: event.target.value })} /><span className="help">Required · kept with this final decision</span></div></fieldset> : <div className="claim-risk-readonly"><StatusBadge tone={flag.status === "confirmed" ? "warn" : "neutral"}>{claimRiskDecisionLabel(flag.status)}</StatusBadge><Kv rows={[["Reviewed by", flag.reviewerName ?? "Name unavailable"], ["Decided", fmtDateTime(flag.decidedAt)], ["Reason", flag.decisionReason ?? "—"]]} /></div>}
    </div>)}
    <div className="section"><p className="muted">Each flag is decided separately. Decisions are kept and cannot be changed.</p>{open.length > 0 ? <button type="button" className="btn primary block" disabled={busy || !ready} aria-busy={busy} onClick={() => void save()}>{busy ? "Saving…" : `Save ${open.length} ${open.length === 1 ? "decision" : "decisions"}`}</button> : <p className="ok-text"><strong>Every flag has a final decision.</strong></p>}</div>
  </>;
}

function percentage(value: number): string {
  return new Intl.NumberFormat(undefined, { style: "percent", maximumFractionDigits: 1 }).format(value);
}

function hoursText(value: number | null | undefined): string {
  if (value == null) return "—";
  return `${Number.isInteger(value) ? value.toFixed(0) : value.toFixed(1)} h`;
}

function oldestText(value: string | null): string {
  if (!value) return "no open flags";
  const days = Math.max(0, Math.floor((Date.now() - new Date(value).getTime()) / 86_400_000));
  return days === 0 ? "oldest today" : `oldest ${days} ${days === 1 ? "day" : "days"}`;
}

function periodRange(period: Period): { from: string; to: string } {
  const now = new Date();
  const to = localDate(now);
  const fromDate = new Date(now.getFullYear(), now.getMonth(), now.getDate() - Number(period) + 1);
  return { from: localDate(fromDate), to };
}

function localDate(value: Date): string {
  const year = value.getFullYear(); const month = String(value.getMonth() + 1).padStart(2, "0"); const day = String(value.getDate()).padStart(2, "0");
  return `${year}-${month}-${day}`;
}
