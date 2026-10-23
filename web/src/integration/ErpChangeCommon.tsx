import { useCallback, useEffect, useState, type ReactNode } from "react";
import { Link, useLocation } from "react-router";
import { ApiError } from "../api/client.ts";
import { useSession } from "../session/session";
import { Breadcrumbs, PageHeader } from "../shell/PageHeader";
import { erpChangeApi } from "./erpChangeApi";
import { erpChangeRights, type ErpChangeRights } from "./erpChangeModel";
import type { ErpChangeRunbookView } from "./erpChangeTypes";
import "./integration.css";
import "./erp-change.css";

export function ErpChangeAccess({ children }: { children: ReactNode }) {
  return erpChangeRights(useSession().roles).read ? children
    : <main className="content"><div className="note bad" role="alert">Tenant administrator, integration operator, item master owner or inventory controller access is required.</div></main>;
}

export function useErpChangeRights(): ErpChangeRights { return erpChangeRights(useSession().roles); }

/** The tenant has at most one ERP change in progress; a 404 from /runs/active means there is none. */
export function useActiveRun() {
  const [run, setRun] = useState<ErpChangeRunbookView | null>(null);
  const [missing, setMissing] = useState(false); const [error, setError] = useState<unknown>(null); const [loading, setLoading] = useState(true);
  const load = useCallback(async (signal?: AbortSignal) => {
    setLoading(true); setError(null);
    try { const next = await erpChangeApi.active(signal); if (!signal?.aborted) { setRun(next); setMissing(false); } }
    catch (reason) {
      if (signal?.aborted) return;
      if (reason instanceof ApiError && reason.status === 404) { setRun(null); setMissing(true); }
      else if (!(reason instanceof DOMException && reason.name === "AbortError")) setError(reason);
    } finally { if (!signal?.aborted) setLoading(false); }
  }, []);
  useEffect(() => { const controller = new AbortController(); void load(controller.signal); return () => controller.abort(); }, [load]);
  const refresh = useCallback(() => load(), [load]);
  return { run, setRun, missing, error, loading, refresh };
}

export function ErpChangePage({ title, eyebrow, sub, actions, run, children }: { title: string; eyebrow: ReactNode; sub: ReactNode; actions?: ReactNode; run: ErpChangeRunbookView | null; children: ReactNode }) {
  return <main className="content" id="main" tabIndex={-1}>
    <Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Integration", to: "/integration" }, { label: "ERP change", to: "/integration/erp-change" }, ...(title === "ERP change" ? [] : [{ label: title }])]} />
    <PageHeader eyebrow={eyebrow} title={title} sub={sub} actions={actions} />
    {run && <ErpChangeTabs run={run} />}
    <div className="stack erp-change-stack">{children}</div>
  </main>;
}

export function ErpChangeTabs({ run }: { run: ErpChangeRunbookView }) {
  const { pathname, hash } = useLocation();
  const tabs: [string, string, number | undefined, boolean][] = [
    ["/integration/erp-change", "Runbook", undefined, false],
    ["/integration/erp-change/master-data", "Master data exceptions", run.openMasterDataExceptions || undefined, run.openMasterDataExceptions > 0],
    ["/integration/erp-change/stock", "Stock differences", run.openDifferences || undefined, run.coveredOpenDifferences > 0],
    ["/integration/erp-change#dual", "Dual-run divergences", run.dualRun?.openDivergences || undefined, (run.dualRun?.agedOpenDivergences ?? 0) > 0],
    ["/integration/erp-change#cutover", "Cutover checklist", undefined, false],
  ];
  return <nav className="tabs subnav erp-change-tabs" aria-label="ERP change">
    {tabs.map(([to, label, count, warn]) => {
      const [path, anchor] = to.split("#");
      const active = pathname === path && (anchor ? hash === `#${anchor}` : !hash || path !== "/integration/erp-change");
      return <Link key={to} to={to} aria-current={active ? "page" : undefined}>{label}{count !== undefined && <span className={`n${warn ? " warn" : ""}`}>{count.toLocaleString()}</span>}</Link>;
    })}
  </nav>;
}

export function NoActiveChange({ control }: { control: boolean }) {
  return <section className="card" aria-labelledby="erp-change-none"><div className="card-header"><h2 id="erp-change-none">No ERP change in progress</h2></div><div className="card-body stack">
    <p>An ERP change moves the tenant to a new ERP in six steps: register it read-only, re-map master data, reconcile stock by unit, run both side by side for at least 30 days, cut outbound over, and archive the old connection. Unit history never moves.</p>
    {control ? <p className="muted">Start one from the runbook below. The new connection is registered read-only with no outbound until cutover.</p> : <p className="muted">A tenant administrator or an integration operator starts an ERP change.</p>}
  </div></section>;
}
