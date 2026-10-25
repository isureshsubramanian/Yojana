import { useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { Pager } from "./Pager";
import { PageSizeSelect } from "./PageSizeSelect";
import { SortHeader } from "./SortHeader";
import { gridPreferenceKey, nextGridSort, readGridPreference, writeGridPreference, type GridSort, type SortDirection } from "./gridPreferences";
import { rowRange } from "./paging";
import type { PageResult } from "./types";
import { windowRows } from "./windowing";

export interface GridColumn<TRow> {
  key: string;
  label: string;
  /** Share of the table width in percent, or a pixel width for fixed columns such as the checkbox. */
  width: number | string;
  numeric?: boolean;
  /** Render the cell; return text, a link, a badge. The grid adds the ellipsis and the row height. */
  render: (row: TRow) => ReactNode;
  /** Adds the serial face. */
  mono?: boolean;
  /** Visually quiet column. */
  muted?: boolean;
  /** Declares a sortable column. Complete-list grids also supply its comparable browser value. */
  sort?: { key: string; defaultDir?: SortDirection; value?: (row: TRow) => string | number | boolean | null };
}

export interface DataGridProps<TRow> {
  columns: GridColumn<TRow>[];
  page: PageResult<TRow>;
  rowId: (row: TRow) => string;
  onPage: (page: number) => void;
  /** Rows the user may select; the selection lives with the caller (it becomes a selection set). */
  selection?: { selected: ReadonlySet<string>; onChange: (selected: Set<string>) => void; label: (row: TRow) => string };
  dense?: boolean | undefined;
  /** Rows carrying a find match are highlighted. */
  isHit?: (row: TRow) => boolean;
  /** Viewport height in pixels when windowing is on; rows beyond it scroll inside the card. */
  viewportHeight?: number;
  /** Shown in the footer before the paging text, for example "Sorted by last event". */
  footerNote?: ReactNode;
  /** Replaces the paging footer, for example "7 of 11 shown · Show all"; pass it only when paging is not needed. */
  footer?: ReactNode;
  emptyText?: string;
  loading?: boolean | undefined;
  describedBy?: string;
  /** House layout E: a row click opens the drawer for that row; links, inputs and buttons inside the row keep their own behaviour. */
  onRowClick?: ((row: TRow) => void) | undefined;
  /** The row the drawer currently shows, highlighted. */
  activeRowId?: string | null | undefined;
  /** Stable screen-and-grid id used for per-user browser preferences. */
  gridId?: string;
  userId?: string;
  sort?: GridSort;
  defaultSort?: GridSort;
  onSort?: (sort: GridSort) => void;
  pageSize?: number;
  defaultPageSize?: number;
  pageSizeOptions?: readonly number[];
  onPageSize?: (size: number) => void;
}

const ROW_HEIGHT = 44;
const DENSE_ROW_HEIGHT = 36;

/**
 * Server-paged grid: shows one page and either an exact total or the continuation contract's "more" signal;
 * never asks for "all". Rows beyond the viewport are windowed so even a 500-row page keeps the DOM small.
 * Selection is reported outward as ids; paging facts come from the page result.
 */
export function DataGrid<TRow>({ columns, page, rowId, onPage, selection, dense, isHit, viewportHeight = 640, footerNote, footer, emptyText = "Nothing matches.", loading, describedBy, onRowClick, activeRowId,
  gridId, userId, sort, defaultSort, onSort, pageSize, defaultPageSize = page.pageSize, pageSizeOptions, onPageSize }: DataGridProps<TRow>) {
  const rowHeight = dense ? DENSE_ROW_HEIGHT : ROW_HEIGHT;
  const viewport = useRef<HTMLDivElement>(null);
  const [scrollTop, setScrollTop] = useState(0);
  const firstSort = columns.find(column => column.sort)?.sort;
  const declaredDefault = defaultSort ?? (firstSort ? { key: firstSort.key, dir: firstSort.defaultDir ?? ("asc" as const) } : { key: "", dir: "asc" as const });
  const preferenceKey = gridId && userId ? gridPreferenceKey(userId, gridId) : null;
  const [localPreference, setLocalPreference] = useState(() => readGridPreference(preferenceKey ?? "", {
    sort: declaredDefault,
    pageSize: defaultPageSize,
  }, preferenceKey && typeof window !== "undefined" ? window.localStorage : null));
  const activeSort = sort ?? localPreference.sort;
  const effectivePageSize = pageSize ?? localPreference.pageSize;
  const completeList = footer !== undefined && !!gridId;
  const [completePage, setCompletePage] = useState(1);
  const screenDefault = declaredDefault.key ? declaredDefault : activeSort;
  const setSort = (next: GridSort) => {
    setCompletePage(1);
    if (onSort) onSort(next);
    else {
      const preference = { ...localPreference, sort: next }; setLocalPreference(preference);
      if (preferenceKey) writeGridPreference(preferenceKey, preference, typeof window === "undefined" ? null : window.localStorage);
    }
  };
  const visibleRows = useMemo(() => {
    if (footer === undefined || !activeSort.key) return page.rows;
    const column = columns.find(candidate => candidate.sort?.key === activeSort.key);
    if (!column?.sort?.value) return page.rows;
    const value = column.sort.value;
    return [...page.rows].sort((left, right) => compare(value(left), value(right)) * (activeSort.dir === "asc" ? 1 : -1));
  }, [activeSort, columns, footer, page.rows]);
  const displayedRows = completeList ? visibleRows.slice((completePage - 1) * effectivePageSize, completePage * effectivePageSize) : visibleRows;
  useEffect(() => { viewport.current?.scrollTo({ top: 0 }); setScrollTop(0); }, [completePage, page.page, visibleRows]);
  const win = windowRows(displayedRows.length, rowHeight, scrollTop, viewportHeight);
  const slice = displayedRows.slice(win.start, win.end);
  const range = rowRange(page.page, page.pageSize, page.total);
  const continuationPaged = page.totalIsExact === false;

  const allOnPage = selection && displayedRows.length > 0 && displayedRows.every((r) => selection.selected.has(rowId(r)));
  const someOnPage = selection && !allOnPage && displayedRows.some((r) => selection.selected.has(rowId(r)));
  const togglePage = () => {
    if (!selection) return;
    const next = new Set(selection.selected);
    for (const r of displayedRows) { if (allOnPage) next.delete(rowId(r)); else next.add(rowId(r)); }
    selection.onChange(next);
  };
  const toggle = (id: string) => {
    if (!selection) return;
    const next = new Set(selection.selected);
    if (next.has(id)) next.delete(id); else next.add(id);
    selection.onChange(next);
  };

  return (
    <>
      <div className="grid-viewport" ref={viewport} style={{ maxHeight: viewportHeight }} onScroll={(e) => setScrollTop(e.currentTarget.scrollTop)} aria-busy={loading}>
        <table className={`table${dense ? " dense" : ""}`} aria-describedby={describedBy}>
          <colgroup>
            {selection && <col style={{ width: 44 }} />}
            {columns.map((c) => <col key={c.key} style={{ width: typeof c.width === "number" ? `${c.width}%` : c.width }} />)}
          </colgroup>
          <thead>
            <tr>
              {selection && (
                <th className="check">
                  <input type="checkbox" aria-label="Select page" checked={!!allOnPage} ref={(el) => { if (el) el.indeterminate = !!someOnPage; }} onChange={togglePage} />
                </th>
              )}
              {columns.map((c) => <th key={c.key} className={c.numeric ? "num" : undefined}
                aria-sort={c.sort ? activeSort.key === c.sort.key ? activeSort.dir === "asc" ? "ascending" : "descending" : "none" : undefined}>
                {c.sort ? <SortHeader label={c.label} column={c.sort} sort={activeSort} screenDefault={screenDefault} onSort={setSort} /> : c.label}
              </th>)}
            </tr>
          </thead>
          <tbody>
            {page.rows.length === 0 && (
              <tr><td colSpan={columns.length + (selection ? 1 : 0)} className="muted" style={{ textAlign: "center", height: 96 }}>{loading ? "Loading…" : emptyText}</td></tr>
            )}
            {win.topPad > 0 && <tr aria-hidden="true" style={{ height: win.topPad }}><td colSpan={columns.length + (selection ? 1 : 0)} style={{ padding: 0, border: 0 }} /></tr>}
            {slice.map((row) => {
              const id = rowId(row);
              const selected = selection?.selected.has(id) ?? false;
              const classes = [isHit?.(row) ? "hit" : "", onRowClick ? "clickable" : "", activeRowId === id ? "sel" : ""].filter(Boolean).join(" ");
              return (
                <tr key={id} aria-selected={selection ? selected : undefined} className={classes || undefined}
                  tabIndex={onRowClick ? 0 : undefined}
                  onKeyDown={onRowClick ? (e) => { if (!(e.target instanceof HTMLElement && e.target.closest("a, input, button, select, textarea")) && (e.key === "Enter" || e.key === " ")) { e.preventDefault(); onRowClick(row); } } : undefined}
                  onClick={onRowClick ? (e) => { if (!(e.target instanceof HTMLElement && e.target.closest("a, input, button, select, textarea"))) onRowClick(row); } : undefined}>
                  {selection && <td className="check"><input type="checkbox" aria-label={`Select ${selection.label(row)}`} checked={selected} onChange={() => toggle(id)} /></td>}
                  {columns.map((c) => (
                    <td key={c.key} className={[c.numeric ? "num" : "", c.mono ? "serial" : "", c.muted ? "muted" : ""].filter(Boolean).join(" ") || undefined}>{c.render(row)}</td>
                  ))}
                </tr>
              );
            })}
            {win.bottomPad > 0 && <tr aria-hidden="true" style={{ height: win.bottomPad }}><td colSpan={columns.length + (selection ? 1 : 0)} style={{ padding: 0, border: 0 }} /></tr>}
          </tbody>
        </table>
      </div>
      {footer !== undefined ? <div className="card-footer"><span>{footer}</span>{(onPageSize || gridId) && <span className="grid-page-controls"><PageSizeSelect value={effectivePageSize} defaultSize={defaultPageSize} options={pageSizeOptions}
        onChange={size => { setCompletePage(1); if (onPageSize) onPageSize(size); else { const preference = { ...localPreference, pageSize: size }; setLocalPreference(preference); if (preferenceKey) writeGridPreference(preferenceKey, preference, typeof window === "undefined" ? null : window.localStorage); } }} />
        {completeList && <Pager page={completePage} pageSize={effectivePageSize} total={visibleRows.length} onPage={(next) => { setCompletePage(next); onPage(next); }} />}</span>}</div> : <div className="card-footer">
        <span>
          {footerNote && <>{footerNote} · </>}
          Rows {range.from.toLocaleString()}–{range.to.toLocaleString()}{continuationPaged ? page.hasMore ? " · more match" : "" : <> of <strong>{page.total.toLocaleString()}</strong></>}
          {selection && selection.selected.size > 0 && <> · {selection.selected.size.toLocaleString()} selected</>}
        </span>
        <span className="grid-page-controls">{onPageSize && <PageSizeSelect value={pageSize ?? page.pageSize} defaultSize={defaultPageSize} options={pageSizeOptions} onChange={onPageSize} />}
          <Pager page={page.page} pageSize={page.pageSize} total={page.total} onPage={onPage}
            showPageCount={!continuationPaged} hasNext={continuationPaged ? page.hasMore : undefined} showMore={continuationPaged} /></span>
      </div>}
    </>
  );
}

function compare(left: string | number | boolean | null, right: string | number | boolean | null): number {
  if (left === right) return 0;
  if (left === null) return -1;
  if (right === null) return 1;
  if (typeof left === "number" && typeof right === "number") return left - right;
  return String(left).localeCompare(String(right), undefined, { numeric: true, sensitivity: "base" });
}
