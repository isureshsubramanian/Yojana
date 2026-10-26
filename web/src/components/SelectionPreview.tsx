import { DataGrid, type GridColumn } from "./DataGrid";
import { FindBar } from "./FindBar";
import { StatusBadge } from "./StatusBadge";
import { useGridState } from "./useGridQuery";
import type { PreviewOutcome, PreviewResult, PreviewRow, Tone } from "./types";

export interface SelectionPreviewProps {
  preview: PreviewResult | null;
  loading?: boolean;
  /** Column captions for the before and after values, from the command: "Current", "New end". */
  currentLabel: string;
  nextLabel: string;
  /** Verb shown for a changed row, from the command: "Amend", "Transfer", "Void". */
  changeLabel: string;
  query: string;
  outcome: PreviewOutcome | null;
  onQuery: (query: string) => void;
  onOutcome: (outcome: PreviewOutcome | null) => void;
  onPage: (page: number) => void;
  onDownload?: (() => void) | undefined;
  /** Where the outcome rows link to, when they do. */
  rowHref?: ((row: PreviewRow) => string) | undefined;
  grid: ReturnType<typeof useGridState>;
  gridId: string;
  userId: string;
}

const outcomeTone: Record<PreviewOutcome, Tone> = { change: "ok", skip: "warn", block: "bad" };

/**
 * Selection-set preview (ADR-0001 "Bulk selection sets"): every unit the set resolves to, what will change for
 * each, and which are skipped or blocked and why, exactly as the command computed it. The per-unit outcome
 * list before commit is the user's partial-failure report; the find bar keeps a thousand rows searchable.
 */
export function SelectionPreview({ preview, loading, currentLabel, nextLabel, changeLabel, query, outcome, onQuery, onOutcome, onPage, onDownload, grid, gridId, userId }: SelectionPreviewProps) {
  const summary = preview?.summary ?? { resolved: 0, change: 0, skip: 0, block: 0 };
  const outcomeLabel: Record<PreviewOutcome, string> = { change: changeLabel, skip: "Skipped", block: "Blocked" };
  const columns: GridColumn<PreviewRow>[] = [
    { key: "serial", label: "Serial", width: 22, mono: true, sort: { key: "serial", defaultDir: "asc" }, render: (r) => r.serial },
    { key: "current", label: currentLabel, width: 17, numeric: true, render: (r) => r.current ?? <span className="muted">—</span> },
    { key: "next", label: nextLabel, width: 17, numeric: true, render: (r) => r.next ?? <span className="muted">—</span> },
    { key: "outcome", label: "Outcome", width: 44, sort: { key: "priority", defaultDir: "asc" }, render: (r) => <StatusBadge tone={outcomeTone[r.outcome]}>{outcomeLabel[r.outcome]}{r.reason && <> · {r.reason}</>}</StatusBadge> },
  ];
  return (
    <section className="card" aria-labelledby="h-preview">
      <div className="card-header">
        <h2 id="h-preview">Preview <span className="meta">computed by the command, not the screen</span></h2>
        {onDownload && <button type="button" className="btn ghost sm" onClick={onDownload}>Download CSV</button>}
      </div>
      <div className="preview-summary">
        <Figure label="Resolved" value={summary.resolved} />
        <Figure label="Will change" value={summary.change} tone="success" />
        <Figure label="Skipped" value={summary.skip} tone="warning" />
        <Figure label="Blocked" value={summary.block} tone="danger" />
      </div>
      <FindBar
        compact
        ariaLabel="Find a serial in this preview"
        placeholder="Find a serial"
        query={query}
        onQuery={onQuery}
        facets={[
          { id: "change", label: changeLabel, count: summary.change },
          { id: "skip", label: "Skip", count: summary.skip },
          { id: "block", label: "Block", count: summary.block },
        ]}
        activeFacet={outcome}
        onFacet={(id) => onOutcome(id as PreviewOutcome | null)}
        matchCount={preview?.matches}
      />
      <DataGrid
        dense
        columns={columns}
        page={preview ?? { rows: [], total: 0, page: 1, pageSize: 50, continuation: null }}
        rowId={(r) => r.id}
        onPage={onPage}
        loading={loading}
        emptyText={preview ? "No units match." : "Resolve a selection set to see its preview."}
        viewportHeight={520}
        gridId={gridId}
        userId={userId}
        sort={grid.sort}
        defaultSort={{ key: "priority", dir: "asc" }}
        onSort={grid.onSort}
        pageSize={grid.pageSize}
        defaultPageSize={50}
        onPageSize={grid.onPageSize}
      />
    </section>
  );
}

function Figure({ label, value, tone }: { label: string; value: number; tone?: "success" | "warning" | "danger" }) {
  return (
    <span className="figure">
      <span className="k">{label}</span>
      <span className="v" style={tone ? { color: `var(--${tone})` } : undefined}>{value.toLocaleString()}</span>
    </span>
  );
}
