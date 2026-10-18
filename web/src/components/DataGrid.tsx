import { useEffect, useRef, useState, type ReactNode } from "react";
import { Pager } from "./Pager";
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
  emptyText?: string;
  loading?: boolean | undefined;
  describedBy?: string;
  /** House layout E: a row click opens the drawer for that row; links, inputs and buttons inside the row keep their own behaviour. */
  onRowClick?: ((row: TRow) => void) | undefined;
  /** The row the drawer currently shows, highlighted. */
  activeRowId?: string | null | undefined;
}

const ROW_HEIGHT = 44;
const DENSE_ROW_HEIGHT = 36;

/**
 * Server-paged grid: shows one page and either an exact total or the continuation contract's "more" signal;
 * never asks for "all". Rows beyond the viewport are windowed so even a 500-row page keeps the DOM small.
 * Selection is reported outward as ids; paging facts come from the page result.
 */
export function DataGrid<TRow>({ columns, page, rowId, onPage, selection, dense, isHit, viewportHeight = 640, footerNote, emptyText = "Nothing matches.", loading, describedBy, onRowClick, activeRowId }: DataGridProps<TRow>) {
  const rowHeight = dense ? DENSE_ROW_HEIGHT : ROW_HEIGHT;
  const viewport = useRef<HTMLDivElement>(null);
  const [scrollTop, setScrollTop] = useState(0);
  useEffect(() => { viewport.current?.scrollTo({ top: 0 }); setScrollTop(0); }, [page.page, page.rows]);
  const win = windowRows(page.rows.length, rowHeight, scrollTop, viewportHeight);
  const slice = page.rows.slice(win.start, win.end);
  const range = rowRange(page.page, page.pageSize, page.total);
  const continuationPaged = page.totalIsExact === false;

  const allOnPage = selection && page.rows.length > 0 && page.rows.every((r) => selection.selected.has(rowId(r)));
  const someOnPage = selection && !allOnPage && page.rows.some((r) => selection.selected.has(rowId(r)));
  const togglePage = () => {
    if (!selection) return;
    const next = new Set(selection.selected);
    for (const r of page.rows) { if (allOnPage) next.delete(rowId(r)); else next.add(rowId(r)); }
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
              {columns.map((c) => <th key={c.key} className={c.numeric ? "num" : undefined}>{c.label}</th>)}
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
      <div className="card-footer">
        <span>
          {footerNote && <>{footerNote} · </>}
          Rows {range.from.toLocaleString()}–{range.to.toLocaleString()}{continuationPaged ? page.hasMore ? " · more match" : "" : <> of <strong>{page.total.toLocaleString()}</strong></>}
          {selection && selection.selected.size > 0 && <> · {selection.selected.size.toLocaleString()} selected</>}
        </span>
        <Pager page={page.page} pageSize={page.pageSize} total={page.total} onPage={onPage}
          showPageCount={!continuationPaged} hasNext={continuationPaged ? page.hasMore : undefined} showMore={continuationPaged} />
      </div>
    </>
  );
}
