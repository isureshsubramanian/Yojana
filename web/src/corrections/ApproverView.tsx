import { useEffect, useState } from "react";
import { Link, useParams } from "react-router";
import { Shortcut, StatusBadge, useModEnter } from "../components";
import { Rail } from "../shell/Drawer";
import { Breadcrumbs } from "../shell/PageHeader";
import { usePeople } from "../people/people";
import { useSession } from "../session/session";
import { ErrorNote, Kv, Section } from "../units/common";
import { fmtDateTime, humanise, outcomeTone, shortId } from "../units/format";
import { commandName, recordedRows, workbenchApi, type WorkbenchRequestRow } from "./workbenchApi";

/**
 * P3-T19: an approver's view of a pending correction. The approver reads the command's stored preview, never a form;
 * approving re-runs the preview on the server and refuses when it changed, and commit runs the existing command.
 */
export function Component() {
  const { requestId = "" } = useParams();
  const session = useSession();
  const [row, setRow] = useState<WorkbenchRequestRow | null>(null);
  const [others, setOthers] = useState<WorkbenchRequestRow[]>([]);
  const [labels, setLabels] = useState<Record<string, string>>({});
  const [commitRoles, setCommitRoles] = useState<Record<string, string[]>>({});
  const [decision, setDecision] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const person = usePeople([row?.initiatedBy, row?.rejectedBy, row?.committedBy, ...(row?.approvals.map((a) => a.userRef) ?? []), ...others.map((o) => o.initiatedBy)]);

  useEffect(() => {
    const controller = new AbortController();
    setError(null);
    Promise.all([workbenchApi.request(requestId, controller.signal), workbenchApi.inbox(0, 10, controller.signal), workbenchApi.choices(controller.signal)])
      .then(([request, inbox, choices]) => { setRow(request); setOthers(inbox.items.filter((item) => item.requestId !== requestId)); setLabels(Object.fromEntries(choices.map((c) => [c.choice, c.label]))); setCommitRoles(Object.fromEntries(choices.map((c) => [c.choice, c.commitRoles ?? []]))); })
      .catch((reason) => { if (!(reason instanceof DOMException && reason.name === "AbortError")) setError(reason); });
    return () => controller.abort();
  }, [requestId]);

  const me = session.user.id;
  const roles = session.roles;
  const initiatedByMe = row?.initiatedBy === me;
  const approvedByMe = !!row?.approvals.some((a) => a.userRef === me);
  const mayApprove = row?.status === "awaiting_approval" && !initiatedByMe && !approvedByMe && roles.some((r) => row.requiredApproverRoles.includes(r));
  // Commit is allowed to the choice's commit roles, the same rule the server applies; approver roles only decide approval.
  const mayCommit = (request: WorkbenchRequestRow | null) => request?.status === "approved" && roles.some((r) => (commitRoles[request.choice] ?? []).includes(r));
  const act = async (action: () => Promise<WorkbenchRequestRow>) => {
    setBusy(true); setError(null);
    try { setRow(await action()); } catch (reason) { setError(reason); } finally { setBusy(false); }
  };
  const approveAndCommit = () => act(async () => {
    const approved = await workbenchApi.approve(requestId, decision || null);
    return mayCommit(approved) ? await workbenchApi.commit(requestId).catch(() => approved) : approved;
  });
  const commitsDirectly = mayCommit(row);
  useModEnter(() => commitsDirectly ? act(() => workbenchApi.commit(requestId)) : approveAndCommit(),
    commitsDirectly ? busy : !mayApprove || busy);

  const preview = row?.preview;
  const changed = preview?.units.filter((u) => u.outcome === "change") ?? [];
  const checks = preview?.units.filter((u) => u.outcome !== "change") ?? [];
  return <>
    <main className="content" id="main" tabIndex={-1}>
      <Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Approvals", to: "/approvals" }, { label: row ? `COR ${shortId(row.requestId)}` : "…" }]} />
      <div className="page-header">
        <div>
          <div className="eyebrow">Correction {row ? shortId(row.requestId) : ""} · {row ? commandName(row.command) : ""} · {row ? humanise(row.status) : ""}{mayApprove ? " · waiting for you" : ""}</div>
          <h1>{row ? labels[row.choice] ?? humanise(row.choice) : "Correction request"}</h1>
          <p className="sub">{row ? <>Initiated in the workbench by {person(row.initiatedBy)} ({roleSummary(row.initiatorRoles)}) {fmtDateTime(row.initiatedAt)} · you see the command's preview, not a form · approving re-runs the preview and commits only if it is unchanged</> : "Loading…"}</p>
        </div>
        <div className="actions">
          <button type="button" className="btn" disabled={!row || row.status !== "awaiting_approval" || !decision.trim() || busy} aria-busy={busy} onClick={() => act(() => workbenchApi.reject(requestId, decision))}>Reject…</button>
          {commitsDirectly
            ? <button type="button" className="btn primary" disabled={busy} aria-busy={busy} onClick={() => act(() => workbenchApi.commit(requestId))}>Commit<Shortcut keys="mod+enter" /></button>
            : <button type="button" className="btn primary" disabled={!mayApprove || busy} aria-busy={busy} onClick={approveAndCommit}>Approve and commit<Shortcut keys="mod+enter" /></button>}
        </div>
      </div>
      <ErrorNote error={error} />
      <div className="cols-3-2 top">
        <div className="stack">
          <section className="card" aria-labelledby="ap-changes">
            <div className="card-header"><h2 id="ap-changes">What changes</h2><span className="meta">{preview?.unitCount ?? 0} units · one transaction</span></div>
            <div className="card-body">{changed.length > 0 ? <div className="diff" style={{ gridTemplateColumns: "minmax(0,1fr)" }}><div><h3>Unit · what the command will do</h3>{changed.map((u) => <div className="row" key={u.unitId} style={{ gridTemplateColumns: "150px minmax(0,1fr)" }}><span className="k serial" title={u.unitId}>{u.serial ?? shortId(u.unitId)}</span><span className="chg">{u.detail ?? "changes"}</span></div>)}</div></div> : <p className="muted">No unit changes in the stored preview.</p>}</div>
            <table className="table dense"><thead><tr><th>Will be recorded</th><th>Detail</th><th className="num">Count</th></tr></thead><tbody>
              {preview && recordedRows(preview).map((r) => <tr key={r.what}><td className="mono">{r.what}</td><td>{r.detail}</td><td className="num">{r.count}</td></tr>)}
            </tbody></table>
          </section>
          <section className="card" aria-labelledby="ap-checks">
            <div className="card-header"><h2 id="ap-checks">Checks the command ran</h2><span className="meta">re-run on approve</span></div>
            <table className="table dense"><tbody>
              {preview?.refusal && <tr><td>{humanise(preview.refusal.rule)}</td><td>{preview.refusal.message}{preview.refusal.unblockingStep ? ` · ${preview.refusal.unblockingStep}` : ""}</td><td><StatusBadge tone="warn">Needs approval</StatusBadge></td></tr>}
              {checks.map((u) => <tr key={u.unitId}><td className="serial">{u.serial ?? shortId(u.unitId)}</td><td>{u.reason ? humanise(u.reason) : ""}{u.unblockingStep ? ` · ${u.unblockingStep}` : ""}</td><td><StatusBadge tone={outcomeTone(u.outcome)}>{humanise(u.outcome)}</StatusBadge></td></tr>)}
              {preview && !preview.refusal && checks.length === 0 && <tr><td>Every unit</td><td>the command found nothing to refuse</td><td><StatusBadge tone="ok">Pass</StatusBadge></td></tr>}
            </tbody></table>
          </section>
        </div>
        <div className="stack">
          <section className="card" aria-labelledby="ap-why"><div className="card-header"><h2 id="ap-why">Why it is wrong</h2></div><div className="card-body">
            {row && <Kv rows={[["Choice", labels[row.choice] ?? humanise(row.choice)], ["Reason code", <span className="mono" key="r">{row.reason}</span>], ["Command", commandName(row.command)]]} />}
            {row?.note && <p style={{ marginTop: 10 }}>{row.note}</p>}
            {row?.unclassifiedDescription && <p style={{ marginTop: 10 }}>{row.unclassifiedDescription}</p>}
          </div></section>
          <section className="card" aria-labelledby="ap-evidence"><div className="card-header"><h2 id="ap-evidence">Evidence</h2><span className="meta">{row?.evidence.length ?? 0} references</span></div><div className="card-body">
            {row?.evidence.length ? row.evidence.map((e, index) => <div className="job" key={`${e.ref}-${index}`}><div><div className="t">{e.ref}</div><div className="s">{e.kind}{e.note ? ` · ${e.note}` : ""}</div></div></div>) : <p className="muted">No evidence attached. Ask the initiator before approving if the reason needs it.</p>}
          </div></section>
          <section className="card" aria-labelledby="ap-path"><div className="card-header"><h2 id="ap-path">Approval path</h2><span className="meta">tenant policy</span></div><div className="card-body">
            {row && <>
              <div className="job"><div><div className="t">Initiator · {roleSummary(row.initiatorRoles)}</div><div className="s">{person(row.initiatedBy)}</div></div><StatusBadge tone="ok">Submitted</StatusBadge></div>
              {row.approvals.map((a) => <div className="job" key={a.userRef}><div><div className="t">{roleSummary(a.roles)}</div><div className="s">{person(a.userRef)} · {fmtDateTime(a.approvedAt)}</div></div><StatusBadge tone="ok">Approved</StatusBadge></div>)}
              {row.status === "awaiting_approval" && <div className="job"><div><div className="t">Waiting for {row.requiredApproverRoles.map(humanise).join(" or ")}</div><div className="s">{mayApprove ? "you" : "another approver"}</div></div><StatusBadge tone="warn">Waiting</StatusBadge></div>}
              {row.status === "committed" && <div className="job"><div><div className="t">Committed</div><div className="s">{person(row.committedBy)} · {fmtDateTime(row.committedAt)}</div></div><StatusBadge tone="ok">Done</StatusBadge></div>}
              {row.status === "rejected" && <div className="job"><div><div className="t">Rejected</div><div className="s">{person(row.rejectedBy)} · {row.decisionNote}</div></div><StatusBadge tone="bad">Rejected</StatusBadge></div>}
              <p className="hint" style={{ marginTop: 8 }}>You cannot approve a correction you initiated, and each approver must be a different person.</p>
            </>}
            {row?.status === "awaiting_approval" && <div className="field" style={{ marginTop: 10 }}><label htmlFor="ap-decision">Decision note <span className="hint">· required to reject</span></label><textarea id="ap-decision" className="textarea" value={decision} onChange={(event) => setDecision(event.target.value)} /></div>}
          </div></section>
          <section className="card" aria-labelledby="ap-others"><div className="card-header"><h2 id="ap-others">Also waiting for you</h2><span className="meta">Inbox · {others.length}</span></div><div className="card-body">
            {others.length ? others.map((o) => <div className="job" key={o.requestId}><div><div className="t"><Link to={`/corrections/requests/${o.requestId}`}>{commandName(o.command)}</Link></div><div className="s">{humanise(o.choice)} · {person(o.initiatedBy)}</div></div><StatusBadge tone="info">Waiting</StatusBadge></div>) : <p className="muted">Nothing else is waiting.</p>}
          </div></section>
        </div>
      </div>
    </main>
    <Rail label="Request"><Section title="Stored preview"><Kv rows={[["Request", row ? shortId(row.requestId, 12) : "—"], ["Preview hash", row ? shortId(row.previewHash, 12) : "—"], ["Status", row ? humanise(row.status) : "—"]]} /></Section><Section title="Boundary"><p className="muted">Approval does not edit anything. It records you as an approver; the existing command then runs with server-recorded approvals and `initiated_via = workbench`.</p></Section></Rail>
  </>;
}

/** The first role and a count; a person holding many roles would otherwise fill the line. */
function roleSummary(roles: string[]): string {
  if (roles.length === 0) return "member";
  return roles.length === 1 ? humanise(roles[0]) : `${humanise(roles[0])} +${roles.length - 1} roles`;
}
