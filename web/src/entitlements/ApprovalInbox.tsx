import { useEffect, useMemo, useState } from "react";
import { Link } from "react-router";
import { DataGrid, KpiStrip, StatusBadge, type GridColumn } from "../components";
import { openDrawer, updateDrawer } from "../shell/drawerStore";
import { Rail } from "../shell/Drawer";
import { Breadcrumbs } from "../shell/PageHeader";
import { usePeople } from "../people/people";
import { ErrorNote, Kv, Section, useActor } from "../units/common";
import { documentLabel, fmtDateTime, humanise, shortId } from "../units/format";
import { entitlementApi } from "./api";
import { daysOld } from "./format";
import type { BulkApprovalRequest, EntitlementRequirement, WarrantyLineException } from "./types";
import { commandName, workbenchApi, type WorkbenchRequestRow } from "../corrections/workbenchApi";

type InboxRow =
  | { id: string; kind: "bulk"; title: string; subject: string; reason: string; units: number; raisedAt: string; status: string; bulk: BulkApprovalRequest }
  | { id: string; kind: "requirement"; title: string; subject: string; reason: string; units: number; raisedAt: string; status: string; requirement: EntitlementRequirement }
  | { id: string; kind: "exception"; title: string; subject: string; reason: string; units: number; raisedAt: string; status: string; exception: WarrantyLineException }
  | { id: string; kind: "correction"; title: string; subject: string; reason: string; units: number; raisedAt: string; status: string; correction: WorkbenchRequestRow };

export function Component() {
  const actor = useActor();
  const [requirements, setRequirements] = useState<EntitlementRequirement[]>([]);
  const [exceptions, setExceptions] = useState<WarrantyLineException[]>([]);
  const [approvals, setApprovals] = useState<BulkApprovalRequest[]>([]);
  const [corrections, setCorrections] = useState<WorkbenchRequestRow[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<unknown>(null);
  const [active, setActive] = useState<string | null>(null);
  const [version, setVersion] = useState(0);
  const person = usePeople([...approvals.map((bulk) => bulk.requestedBy), ...corrections.map((row) => row.initiatedBy)]);
  useEffect(() => {
    const controller = new AbortController(); setLoading(true); setError(null);
    Promise.all([entitlementApi.requirements("exception", 0, 100, controller.signal), entitlementApi.exceptions("open", 0, 100, controller.signal), entitlementApi.approvalRequests("pending", 0, 100, controller.signal), workbenchApi.inbox(0, 100, controller.signal)])
      .then(([requirementPage, exceptionPage, approvalPage, correctionPage]) => { setRequirements(requirementPage.items); setExceptions(exceptionPage.items); setApprovals(approvalPage.items); setCorrections(correctionPage.items); setLoading(false); })
      .catch((reason) => { if (!(reason instanceof DOMException && reason.name === "AbortError")) { setError(reason); setLoading(false); } });
    return () => controller.abort();
  }, [version]);

  const rows = useMemo<InboxRow[]>(() => [
    ...corrections.map((row): InboxRow => ({ id: `correction:${row.requestId}`, kind: "correction", title: commandName(row.command), subject: `${humanise(row.choice)} · by ${person(row.initiatedBy)}`, reason: row.reason, units: row.preview.unitCount, raisedAt: row.initiatedAt, status: row.status === "approved" ? "Ready to commit" : row.status === "unclassified" ? "Re-file" : "Needs approval", correction: row })),
    ...approvals.map((bulk): InboxRow => ({ id: `bulk:${bulk.requestId}`, kind: "bulk", title: humanise(bulk.operation), subject: `${documentLabel(bulk.sourceDocument)} · by ${person(bulk.requestedBy)}`, reason: bulk.rules.join(", ") || "approval_required", units: bulk.block, raisedAt: bulk.requestedAt, status: "Needs approval", bulk })),
    ...requirements.map((requirement): InboxRow => ({ id: `requirement:${requirement.requirementId}`, kind: "requirement", title: "Entitlement requirement", subject: `Unit ${shortId(requirement.unitId)}${requirement.sourceDocument ? ` · ${documentLabel(requirement.sourceDocument)}` : ""}`, reason: requirement.missingInput ?? "creation_failed", units: 1, raisedAt: requirement.raisedAt, status: "Manager decision", requirement })),
    ...exceptions.map((exception): InboxRow => ({ id: `exception:${exception.exceptionId}`, kind: "exception", title: "Warranty-line exception", subject: documentLabel(exception.document), reason: exception.missingInput, units: 1, raisedAt: exception.raisedAt, status: "Resolve targets", exception })),
  ].sort((left, right) => left.raisedAt.localeCompare(right.raisedAt)), [approvals, requirements, exceptions, corrections, person]);
  const oldest = rows[0] ? daysOld(rows[0].raisedAt) : 0;
  const columns: GridColumn<InboxRow>[] = [
    { key: "request", label: "Request · subject", width: 34, render: (row) => <>{row.title}<span className="sub">{row.subject}</span></> },
    { key: "source", label: "Source", width: 15, render: (row) => <StatusBadge tone={row.kind === "bulk" || row.kind === "correction" ? "info" : row.kind === "requirement" ? "bad" : "warn"}>{humanise(row.kind)}</StatusBadge> },
    { key: "reason", label: "Why this needs attention", width: 24, mono: true, render: (row) => row.reason },
    { key: "units", label: "Units", width: 8, numeric: true, render: (row) => row.units.toLocaleString() },
    { key: "status", label: "Status · age", width: 19, render: (row) => <><StatusBadge tone={row.kind === "requirement" ? "bad" : "warn"}>{row.status}</StatusBadge><span className="sub">{daysOld(row.raisedAt)} d · {fmtDateTime(row.raisedAt)}</span></> },
  ];
  const open = (row: InboxRow) => {
    setActive(row.id);
    if (row.kind === "correction") {
      openDrawer({ eyebrow: `Correction · ${commandName(row.correction.command)}`, title: shortId(row.correction.requestId, 12), mono: true }, <><h3>Request</h3><Kv rows={[["Choice", humanise(row.correction.choice)], ["Reason", row.correction.reason], ["Units", row.correction.preview.unitCount.toLocaleString()], ["Initiated by", person(row.correction.initiatedBy)], ["Raised", fmtDateTime(row.correction.initiatedAt)], ["Waiting for", row.correction.requiredApproverRoles.map(humanise).join(" or ")], ["Note", row.correction.note ?? "—"]]} /><div className="act"><Link className="btn primary block" to={`/corrections/requests/${row.correction.requestId}`}>Open preview to approve or reject</Link></div><p className="hint">Approval re-runs the command's preview and refuses when anything changed since it was submitted.</p></>);
    } else if (row.kind === "bulk") {
      openDrawer({ eyebrow: `Bulk approval · ${humanise(row.bulk.operation)}`, title: shortId(row.bulk.requestId, 12), mono: true }, <><h3>Request</h3><Kv rows={[["Operation", row.bulk.operation], ["Selection", shortId(row.bulk.selectionId, 12)], ["Source document", documentLabel(row.bulk.sourceDocument)], ["Units", `${row.bulk.total.toLocaleString()} · ${row.bulk.block.toLocaleString()} need approval · ${row.bulk.skip.toLocaleString()} skip`], ["Rule", row.bulk.rules.join(", ")], ["Requested by", person(row.bulk.requestedBy)], ["Raised", fmtDateTime(row.bulk.requestedAt)], ["Note", row.bulk.note ?? "—"]]} /><div className="act"><Link className="btn primary block" to={`/entitlements/bulk?approval=${encodeURIComponent(row.bulk.requestId)}`}>Open preview to approve or reject</Link></div><p className="hint">Approval re-checks this preview on the server and executes only if no unit has changed since it was raised.</p></>);
    } else if (row.kind === "requirement") {
      openDrawer({ eyebrow: "Entitlement requirement · manager action", title: shortId(row.requirement.requirementId, 12), mono: true }, <RequirementDecision row={row.requirement} actor={actor} onDone={() => setVersion((value) => value + 1)} />);
    } else {
      openDrawer({ eyebrow: "Warranty-line exception · target resolution", title: shortId(row.exception.document.id, 12), mono: true }, <><h3>Exception</h3><Kv rows={[["Document", documentLabel(row.exception.document)], ["Item", shortId(row.exception.itemId, 12)], ["Missing input", humanise(row.exception.missingInput)], ["Detail", row.exception.detail ?? "—"], ["Original trigger", fmtDateTime(row.exception.triggerAt)]]} /><div className="act"><Link className="btn primary block" to="/entitlements/exceptions">Open exception queue</Link></div></>);
    }
  };

  return <>
    <main className="content" id="main" tabIndex={-1}>
      <Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Approvals" }]} />
      <div className="page-header"><div><h1>Approvals</h1><p className="sub">Corrections and entitlement changes waiting for an approver · thresholds by reason code and days changed are tenant data · no change applies before approval</p></div><div className="actions"><Link className="btn" to="/entitlements/exceptions">Ask for evidence…</Link><Link className="btn primary" to="/entitlements/bulk">Open approval…</Link></div></div>
      <ErrorNote error={error} />
      <div className="stack">
        <KpiStrip kpis={[{ label: "Waiting for you", value: rows.length.toLocaleString(), detail: `${corrections.length} corrections · ${approvals.length} bulk · ${rows.length - approvals.length - corrections.length} single`, trend: null }, { label: "Oldest", value: `${oldest} d`, detail: rows[0]?.title ?? "Nothing waiting", trend: null }, { label: "Due today", value: requirements.length.toLocaleString(), detail: "manager requirements on this page", trend: null }, { label: "Bulk approvals", value: approvals.length.toLocaleString(), detail: "frozen previews · server", trend: null }]} />
        <section className="card" aria-labelledby="approval-list"><div className="card-header"><h2 id="approval-list">Requests <span className="meta">{rows.length} waiting · sorted oldest first</span></h2><div className="segmented plain" aria-label="Approval scope"><button type="button" aria-pressed="true">Mine <span className="n">{rows.length}</span></button><button type="button">Finance review</button><button type="button">All open</button></div></div><DataGrid columns={columns} page={{ rows, total: rows.length, page: 1, pageSize: Math.max(rows.length, 1), continuation: null }} rowId={(row) => row.id} onPage={() => undefined} onRowClick={open} activeRowId={active} loading={loading} emptyText="Nothing is waiting for your approval. New requests will appear here with the command preview and required evidence." viewportHeight={620} /></section>
      </div>
    </main>
    <Rail label="Approval sources"><Section title="What appears here"><div className="job"><div><div className="t">Entitlement requirements</div><div className="s">backend · exception status</div></div><StatusBadge tone="bad">{requirements.length}</StatusBadge></div><div className="job"><div><div className="t">Warranty-line exceptions</div><div className="s">backend · open status</div></div><StatusBadge tone="warn">{exceptions.length}</StatusBadge></div><div className="job"><div><div className="t">Bulk previews</div><div className="s">backend · pending status</div></div><StatusBadge tone="info">{approvals.length}</StatusBadge></div></Section><Section title="Boundary"><p className="muted">The screen does not invent approval state. Every row here is persisted on the server. A bulk request executes the requester's exact arguments under your own roles, and never after its units have changed.</p></Section></Rail>
  </>;
}

function RequirementDecision({ row, actor, onDone }: { row: EntitlementRequirement; actor: string; onDone: () => void }) {
  const [reason, setReason] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const waive = async () => { setBusy(true); setError(null); try { const result = await entitlementApi.waiveRequirement(row.requirementId, reason, actor); updateDrawer({ eyebrow: "Requirement waived", title: shortId(result.requirementId, 12), mono: true }, <><h3>Decision recorded</h3><Kv rows={[["Status", humanise(result.status)], ["Reason", reason], ["Unit", shortId(result.unitId, 12)]]} /></>); onDone(); } catch (problem) { setError(problem); } finally { setBusy(false); } };
  return <><h3>Requirement</h3><Kv rows={[["Unit", shortId(row.unitId, 12)], ["Item", shortId(row.itemId, 12)], ["Source", row.sourceDocument ? documentLabel(row.sourceDocument) : "—"], ["Missing input", humanise(row.missingInput)], ["Detail", row.detail ?? "—"], ["Raised", fmtDateTime(row.raisedAt)]]} /><div className="act"><Link className="btn block" to={`/units/${row.unitId}`}>Open unit</Link></div><h3 style={{ marginTop: 16 }}>Waive</h3><label className="lbl" htmlFor={`waive-${row.requirementId}`}>Reason</label><textarea id={`waive-${row.requirementId}`} className="textarea" value={reason} onChange={(event) => setReason(event.target.value)} /><ErrorNote error={error} /><div className="act"><button type="button" className="btn primary block" disabled={!reason.trim() || busy} aria-busy={busy} onClick={waive}>Waive requirement</button></div><p className="hint">Waiving is an audited manager decision. Resolve the missing policy or evidence instead when coverage is still owed.</p></>;
}
