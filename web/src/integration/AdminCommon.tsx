import { useCallback, useEffect, useRef, useState, type ReactNode } from "react";
import { DataGrid, StatusBadge, useGridState, type GridColumn, type GridSort, type PageResult } from "../components";
import { Breadcrumbs, PageHeader } from "../shell/PageHeader";
import { ErrorNote } from "../units/common";
import { useSession } from "../session/session";
import { IntegrationTabs, useIntegrationTabCounts } from "./Common";
import { human, localDate, mayOperateIntegration, maySeeIntegrationIndicator } from "./model";
import { adminApi } from "./adminApi";
import type { ConnectionDetail, CursorPage } from "./adminTypes";
import "./integration.css";

export const date = (value: string | null | undefined) => value ? localDate(value) : "—";
export function State({ value }: { value: string }) { return <StatusBadge tone={/^(healthy|clean|matched|completed|confirmed|connected|native|active)$/.test(value) ? "ok" : /down|fail|mismatch|difference|revoked/.test(value) ? "bad" : /degraded|attention|awaiting|pending|stale/.test(value) ? "warn" : "neutral"}>{{ clean: "Matched", mismatch: "Differences", awaiting_reconciliation: "Awaiting daily check", native: "Built in" }[value] ?? human(value)}</StatusBadge>; }
export function RoleAccess({ children, status = false }: { children: ReactNode; status?: boolean }) {
  const roles = useSession().roles;
  return (status ? maySeeIntegrationIndicator(roles) : mayOperateIntegration(roles)) ? children : <main className="content"><div className="note bad" role="alert">Integration access is required.</div></main>;
}
export function Frame({ title, sub, actions, children }: { title: string; sub: string; actions?: ReactNode; children: ReactNode }) {
  const counts = useIntegrationTabCounts();
  return <main className="content" id="main" tabIndex={-1}><Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Integration", to: "/integration" }, { label: title }]} /><PageHeader eyebrow="Integration" title={title} sub={sub} actions={actions} /><IntegrationTabs {...counts} /><div className="stack">{children}</div></main>;
}
export function Panel({ title, children, actions }: { title: string; children: ReactNode; actions?: ReactNode }) { return <section className="card" aria-label={title}><div className="card-header"><h2>{title}</h2><div className="integration-card-actions">{actions}</div></div><div className="card-body stack">{children}</div></section>; }
interface CursorGridOptions {
  gridId: string;
  userId: string;
  defaultSort: GridSort;
  defaultSize: number;
  pageSizeOptions?: readonly number[];
}

export function useCursorPage<T, P extends CursorPage<T> = CursorPage<T>>(fetchPage: (cursor?: string, signal?: AbortSignal,
  grid?: { sort: GridSort; pageSize: number }) => Promise<P>, options?: CursorGridOptions) {
  const [result, setResult] = useState<P | null>(null); const [page, setPage] = useState(1); const [cursor, setCursor] = useState<string | undefined>();
  const [busy, setBusy] = useState(true); const [error, setError] = useState<unknown>(null); const [revision, setRevision] = useState(0);
  const cursors = useRef<Array<string | undefined>>([undefined]);
  const gridState = useGridState(options?.gridId ?? "integration-cursor-grid", options?.userId ?? "anonymous",
    options?.defaultSort ?? { key: "record_order", dir: "asc" }, options?.defaultSize ?? 12);
  useEffect(() => { setCursor(undefined); setPage(1); setResult(null); cursors.current = [undefined]; }, [fetchPage]);
  useEffect(() => {
    const controller = new AbortController(); setBusy(true); setError(null);
    fetchPage(cursor, controller.signal, { sort: gridState.sort, pageSize: gridState.pageSize }).then((next) => { if (!controller.signal.aborted) setResult(next); }).catch((reason: unknown) => { if (!controller.signal.aborted) setError(reason); }).finally(() => { if (!controller.signal.aborted) setBusy(false); });
    return () => controller.abort();
  }, [fetchPage, cursor, revision, gridState.sort, gridState.pageSize]);
  const onPage = (target: number) => { if (busy) return; const next = target > page ? result?.nextCursor ?? undefined : cursors.current[target - 1]; if (target > page && !next) return; cursors.current[target - 1] = next; setCursor(next); setPage(target); };
  const refresh = useCallback(() => setRevision(x => x + 1), []);
  const pageSize = options?.defaultSize ? gridState.pageSize : 12;
  const grid: PageResult<T> = { rows: result?.items ?? [], total: (page - 1) * pageSize + (result?.items.length ?? 0), page, pageSize, continuation: result?.nextCursor ?? null, totalIsExact: false, hasMore: !!result?.nextCursor };
  return { result, busy, error, grid, onPage, refresh, gridOptions: options ? { gridId: options.gridId, userId: options.userId,
    sort: gridState.sort, defaultSort: options.defaultSort, onSort: gridState.onSort, pageSize: gridState.pageSize,
    defaultPageSize: options.defaultSize, pageSizeOptions: options.pageSizeOptions, onPageSize: gridState.onPageSize } : undefined };
}
export function PagedGrid<T>({ source, columns, rowId, select, selected, empty }: { source: ReturnType<typeof useCursorPage<T>>; columns: GridColumn<T>[]; rowId: (row: T) => string; select?: ((row: T) => void) | undefined; selected?: string | undefined; empty: string }) {
  return <><ErrorNote error={source.error} />{source.error && <button className="btn sm" onClick={source.refresh}>Try again</button>}<DataGrid columns={columns} page={source.grid} rowId={rowId} onPage={source.onPage} loading={source.busy} onRowClick={select} activeRowId={selected} emptyText={empty} viewportHeight={430} {...source.gridOptions} /></>;
}
export function ConnectionPicker({ value, onChange }: { value: string; onChange: (value: string) => void }) {
  const fetch = useCallback((cursor?: string, signal?: AbortSignal) => adminApi.connections(cursor, false, signal), []);
  const source = useCursorPage(fetch);
  return <div className="toolbar connection-picker"><label className="field"><span className="lbl">Connection</span><select className="select" value={source.result?.items.some(x => x.connectionId === value) ? value : ""} onChange={e => onChange(e.target.value)}><option value="">All connections</option>{source.result?.items.map(x => <option key={x.connectionId} value={x.connectionId}>{x.connectionName}</option>)}</select></label><button className="btn" disabled={source.busy || source.grid.page === 1} onClick={() => { onChange(""); source.onPage(source.grid.page - 1); }}>Previous connections</button><button className="btn" disabled={source.busy || !source.grid.hasMore} onClick={() => { onChange(""); source.onPage(source.grid.page + 1); }}>More connections</button><ErrorNote error={source.error} /></div>;
}
export function useAction() {
  const [busy, setBusy] = useState(false); const [error, setError] = useState<unknown>(null); const [notice, setNotice] = useState("");
  async function run(action: () => Promise<unknown>, message = "Saved.") { setBusy(true); setError(null); setNotice(""); try { await action(); setNotice(message); } catch (reason) { setError(reason); } finally { setBusy(false); } }
  return { busy, run, feedback: <><ErrorNote error={error} /><span role="status">{notice}</span></> };
}

export function useConnectionDetail(id: string) {
  const [detail, setDetail] = useState<ConnectionDetail | null>(null); const [error, setError] = useState<unknown>(null);
  useEffect(() => {
    setDetail(null); setError(null); if (!id) return;
    const controller = new AbortController();
    adminApi.detail(id, controller.signal).then(value => { if (!controller.signal.aborted) setDetail(value); })
      .catch(reason => { if (!controller.signal.aborted) setError(reason); });
    return () => controller.abort();
  }, [id]);
  return { detail: detail?.connection.connectionId === id ? detail : null, error };
}
