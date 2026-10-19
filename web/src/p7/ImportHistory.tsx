import { useCallback, useEffect, useMemo, useState } from "react";
import { Link } from "react-router";
import { DataGrid, FindBar, StatusBadge, type GridColumn, type PageResult } from "../components";
import { Icon } from "../shell/Icons";
import { Breadcrumbs, PageHeader } from "../shell/PageHeader";
import { ErrorNote } from "../units/common";
import { p7Api, query } from "./api";
import { date, human, tone } from "./model";
import type { ImportFileHistoryRow } from "./types";

function pageOf(rows: ImportFileHistoryRow[], page: number, hasMore: boolean): PageResult<ImportFileHistoryRow> {
  return { rows, total: rows.length + (hasMore ? 1 : 0), page, pageSize: Math.max(rows.length, 1), continuation: null, totalIsExact: !hasMore, hasMore };
}
function bytes(value: number) { if (value < 1024) return `${value} B`; if (value < 1024 ** 2) return `${(value / 1024).toFixed(1)} KB`; return `${(value / 1024 ** 2).toFixed(1)} MB`; }

const notPermitted = "only the uploader, an administrator or the service manager can download it";

function convertedName(fileName: string) { return fileName.replace(/\.[^.]+$/, "") + ".xlsx"; }

/** One download icon; disabled (still showing why in its tooltip) when the viewer may not download, empty slot when there is no such copy. */
function FileDownload({ row, copy, available }: { row: ImportFileHistoryRow; copy: "original" | "converted"; available: boolean }) {
  if (!available) return <span className="p7-file-btn empty" aria-hidden="true" />;
  const icon = copy === "original" ? "import" : "converted";
  const what = copy === "original" ? `Download original file (${row.fileName})` : `Download converted copy (${convertedName(row.fileName)})`;
  if (!row.mayDownload) {
    const label = `${what}: ${notPermitted}`;
    return <span className="p7-file-btn disabled" role="img" aria-label={label} title={label}><Icon name={icon} /></span>;
  }
  return <a className="p7-file-btn" href={`/api/imports/${row.jobId}/files/${copy}`} aria-label={what} title={what}><Icon name={icon} /></a>;
}

const sources: Record<string, string> = { user_upload: "User upload", three_pl_feed: "3PL feed", vendor_asn: "Vendor ASN", edi: "EDI" };
const sourceLabel = (source: string) => sources[source] ?? human(source);

export function Component() {
  const [rows, setRows] = useState<ImportFileHistoryRow[]>([]); const [cursor, setCursor] = useState<string | null>(null);
  const [cursors, setCursors] = useState<Array<string | undefined>>([undefined]); const [page, setPage] = useState(1);
  const [find, setFind] = useState(""); const [source, setSource] = useState(""); const [status, setStatus] = useState("");
  const [uploader, setUploader] = useState(""); const [from, setFrom] = useState(""); const [to, setTo] = useState("");
  const [busy, setBusy] = useState(false); const [error, setError] = useState<unknown>(null);
  const filters = useMemo(() => ({ from: from ? `${from}T00:00:00Z` : undefined, to: to ? `${to}T23:59:59.999Z` : undefined,
    source: source || undefined, status: status || undefined, uploader: uploader || undefined,
    fileName: find && !/^[a-f0-9]{64}$/i.test(find) ? find : undefined, hash: /^[a-f0-9]{64}$/i.test(find) ? find.toLowerCase() : undefined }), [find, source, status, uploader, from, to]);
  const load = useCallback(async (target = 1, after?: string) => { setBusy(true); setError(null); try { const result = await p7Api.importHistory(filters, after); setRows(result.items); setCursor(result.nextCursor); setPage(target); if (result.nextCursor) setCursors((current) => { const next = current.slice(0, target); next[target] = result.nextCursor!; return next; }); } catch (reason) { setError(reason); } finally { setBusy(false); } }, [filters]);
  useEffect(() => { setCursors([undefined]); void load(); }, [load]);
  const uploaderOptions = useMemo(() => Array.from(new Map(rows.map((row) => [row.uploaderReference, row.uploaderName])).entries()).sort((a, b) => a[1].localeCompare(b[1])), [rows]);
  const columns: GridColumn<ImportFileHistoryRow>[] = [
    { key: "file", label: "File", width: 20, render: (row) => <><span className="primary">{row.fileName}</span><span className="sub">{row.format.toUpperCase()} · {bytes(row.size)}</span></> },
    { key: "uploaded", label: "Uploaded", width: 13, render: (row) => <><span className="primary">{date(row.uploadedAt)}</span><span className="sub">{row.uploaderName}</span></> },
    { key: "source", label: "Source", width: 9, render: (row) => sourceLabel(row.source) },
    { key: "status", label: "Status", width: 9, render: (row) => <StatusBadge tone={tone(row.status)}>{human(row.status)}</StatusBadge> },
    { key: "rows", label: "Rows", width: 12, render: (row) => <><span className="primary">{row.applied} applied</span><span className="sub">{row.skipped} skipped · {row.rejected} rejected</span></> },
    { key: "hash", label: "SHA-256", width: 13, render: (row) => <><span className="serial" title={row.originalHash}>{row.originalHash.slice(0, 12)}…</span>{row.convertedHash && <span className="sub" title={row.convertedHash}>Converted {row.convertedHash.slice(0, 8)}…</span>}</> },
    { key: "storage", label: "Storage", width: 8, render: (row) => <StatusBadge tone={row.storageState === "archived" ? "neutral" : "ok"}>{human(row.storageState)}</StatusBadge> },
    { key: "download", label: "Files", width: 7, render: (row) => <span className="p7-file-actions">
      <FileDownload row={row} copy="original" available={row.originalAvailable} />
      <FileDownload row={row} copy="converted" available={row.convertedAvailable} />
    </span> },
  ];
  const exportPath = `/api/imports/history/export${query(filters)}`;
  return <main className="content" id="main" tabIndex={-1}><Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Imports", to: "/imports" }, { label: "Upload history" }]} />
    <PageHeader eyebrow="Data · Imports" title="Upload history" sub="Every retained upload, its integrity hashes, row outcome and storage state." actions={<><Link className="btn" to="/imports">Open import jobs</Link><a className="btn" href={exportPath}>Export CSV</a></>} />
    <ErrorNote error={error} /><FindBar query={find} onQuery={setFind} facets={[]} activeFacet={null} onFacet={() => undefined} ariaLabel="Find by file name or full SHA-256 hash" placeholder="Find by file name or full SHA-256 hash" status={`${rows.length} files on this page`} />
    <div className="p7-history-filters"><label>From<input className="input" type="date" value={from} onChange={(e) => setFrom(e.target.value)} /></label><label>To<input className="input" type="date" value={to} onChange={(e) => setTo(e.target.value)} /></label><label>Source<select className="select" value={source} onChange={(e) => setSource(e.target.value)}><option value="">All sources</option>{["user_upload", "three_pl_feed", "vendor_asn", "edi"].map((x) => <option key={x} value={x}>{sourceLabel(x)}</option>)}</select></label><label>Status<select className="select" value={status} onChange={(e) => setStatus(e.target.value)}><option value="">All statuses</option>{["intake", "compared", "reviewed", "handed_off", "applied", "partial", "rejected"].map((x) => <option key={x} value={x}>{human(x)}</option>)}</select></label><label>Uploader<select className="select" value={uploader} onChange={(e) => setUploader(e.target.value)}><option value="">All uploaders</option>{uploaderOptions.map(([ref, name]) => <option key={ref} value={ref}>{name}</option>)}</select></label></div>
    <section className="card top"><DataGrid columns={columns} page={pageOf(rows, page, !!cursor)} rowId={(row) => row.jobId} onPage={(next) => void load(next, cursors[next - 1])} loading={busy} emptyText="No uploaded files match these filters." /></section></main>;
}
