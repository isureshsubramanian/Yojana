import { useEffect, useMemo, useState, type Dispatch, type SetStateAction } from "react";
import { Link } from "react-router";
import { CollapsibleCard, DataGrid, FindBar, StatusBadge, useGridQuery, type GridColumn, type GridSort, type PageResult } from "../components";
import { useSession } from "../session/session";
import { Icon } from "../shell/Icons";
import { Breadcrumbs, PageHeader } from "../shell/PageHeader";
import { ErrorNote } from "../units/common";
import { query } from "./api";
import { date, human, tone } from "./model";
import { comparisonHistoryPageSchema, feedMessageHistoryPageSchema, importFileHistoryPageSchema, type ComparisonHistoryRow, type ComparisonInputFile, type FeedMessageHistoryRow, type ImportFileHistoryRow } from "./types";

const PAGE_SIZE = 50;
const JOB_STATUSES = ["intake", "compared", "reviewed", "handed_off", "applied", "partial", "rejected"];
const FEED_STATUSES = ["applied", "partial", "rejected"];

function pageOf<T>(rows: T[], page: number, pageSize: number, hasMore: boolean): PageResult<T> {
  return { rows, total: (page - 1) * pageSize + rows.length + (hasMore ? 1 : 0), page, pageSize,
    continuation: null, totalIsExact: !hasMore, hasMore };
}
function rowsMeta(rows: number, page: number): string {
  if (!rows) return "Rows 0–0";
  const first = (page - 1) * PAGE_SIZE + 1;
  return `Rows ${first.toLocaleString()}–${(first + rows - 1).toLocaleString()}`;
}
function historyFilters(find: string, from: string, to: string, status: string, uploader?: string) {
  return { find: find || undefined, from: from ? `${from}T00:00:00Z` : undefined,
    to: to ? `${to}T23:59:59.999Z` : undefined, status: status || undefined, uploader: uploader || undefined };
}
function StatusSelect({ value, onChange, values }: { value: string; onChange: (value: string) => void; values: string[] }) {
  return <label className="p7-card-filter">Status<select className="select" value={value} onChange={(event) => onChange(event.target.value)}>
    <option value="">All statuses</option>{values.map((item) => <option value={item} key={item}>{human(item)}</option>)}</select></label>;
}
function ExportCsv({ path }: { path: string }) { return <a className="btn sm" href={path}><Icon name="down" />Export CSV</a>; }
function badge(value: string) { return <StatusBadge tone={tone(value)}>{human(value)}</StatusBadge>; }

function FileDownload({ row, copy, available }: { row: ImportFileHistoryRow; copy: "original" | "converted"; available: boolean }) {
  const permitted = available && row.mayDownload;
  const label = !available ? `${human(copy)} file is not available` : row.mayDownload ? `Download ${copy} file` : `Only the uploader, an administrator or a service manager may download this file`;
  return permitted ? <a className="p7-file-btn" href={`/api/imports/${row.jobId}/files/${copy}`} aria-label={label} title={label}><Icon name={copy === "original" ? "file" : "converted"} /></a>
    : <span className={`p7-file-btn disabled${available ? "" : " p7-file-slot"}`} aria-disabled="true" aria-label={label} title={label}><Icon name={copy === "original" ? "file" : "converted"} /></span>;
}

interface ContentProps { find: string; from: string; to: string; status: string; setMeta: Dispatch<SetStateAction<string>>; setSort: Dispatch<SetStateAction<GridSort>> }

function UploadedFilesContent({ find, from, to, status, uploader, setMeta, setSort, setUploaders }: ContentProps & {
  uploader: string; setUploaders: Dispatch<SetStateAction<Array<[string, string]>>> }) {
  const session = useSession();
  const filters = useMemo(() => historyFilters(find, from, to, status, uploader), [find, from, to, status, uploader]);
  const params = useMemo(() => new URLSearchParams(query(filters)), [filters]);
  const grid = useGridQuery("/api/imports/history", importFileHistoryPageSchema, { gridId: "import-files-history", userId: session.user.id, defaultSort: { key: "uploaded", dir: "desc" }, defaultSize: PAGE_SIZE, params, paging: "cursor", pageFacts: (data) => ({ continuation: data.nextCursor }), sortKeys: ["file", "uploaded", "status"] });
  const rows = grid.data?.items ?? [];
  useEffect(() => setMeta(rowsMeta(rows.length, grid.page)), [rows.length, grid.page, setMeta]);
  useEffect(() => setSort(grid.sort), [grid.sort, setSort]);
  useEffect(() => setUploaders(Array.from(new Map(rows.map((row) => [row.uploaderReference, row.uploaderName])).entries()).sort((a, b) => a[1].localeCompare(b[1]))), [rows, setUploaders]);
  const columns: GridColumn<ImportFileHistoryRow>[] = [
    { key: "file", label: "File", width: 20, sort: { key: "file", defaultDir: "asc" }, render: (row) => <><span className="primary">{row.fileName}</span><span className="sub">{row.format.toUpperCase()} · {row.size.toLocaleString()} bytes</span></> },
    { key: "uploaded", label: "Uploaded", width: 13, sort: { key: "uploaded", defaultDir: "desc" }, render: (row) => <><span className="primary">{date(row.uploadedAt)}</span><span className="sub">{row.uploaderName}</span></> },
    { key: "source", label: "Source", width: 9, render: (row) => human(row.source) },
    { key: "status", label: "Status", width: 9, sort: { key: "status", defaultDir: "asc" }, render: (row) => badge(row.status) },
    { key: "rows", label: "Rows", width: 12, render: (row) => <><span className="primary">{row.applied} applied</span><span className="sub">{row.skipped} skipped · {row.rejected} rejected</span></> },
    { key: "hash", label: "SHA-256", width: 13, render: (row) => <><span className="serial" title={row.originalHash}>{row.originalHash.slice(0, 12)}…</span>{row.convertedHash && <span className="sub" title={row.convertedHash}>Converted {row.convertedHash.slice(0, 8)}…</span>}</> },
    { key: "storage", label: "Storage", width: 8, render: (row) => <>{badge(row.storageState)}{row.archiveStoreName && <span className="sub">{row.archiveStoreName}</span>}</> },
    { key: "download", label: "Files", width: 7, render: (row) => <span className="p7-file-actions"><FileDownload row={row} copy="original" available={row.originalAvailable} /><FileDownload row={row} copy="converted" available={row.convertedAvailable} /></span> },
  ];
  return <><ErrorNote error={grid.error} /><DataGrid columns={columns} page={pageOf(rows, grid.page, grid.pageSize, !!grid.data?.nextCursor)} rowId={(row) => row.jobId}
    onPage={grid.onPage} loading={grid.loading} emptyText="No uploaded files match these filters." gridId="import-files-history" userId={session.user.id} sort={grid.sort} defaultSort={{ key: "uploaded", dir: "desc" }} onSort={grid.onSort} pageSize={grid.pageSize} defaultPageSize={PAGE_SIZE} onPageSize={grid.onPageSize} /></>;
}

function FeedMessagesContent({ find, from, to, status, setMeta, setSort }: ContentProps) {
  const session = useSession();
  const filters = useMemo(() => historyFilters(find, from, to, status), [find, from, to, status]);
  const params = useMemo(() => new URLSearchParams(query(filters)), [filters]);
  const grid = useGridQuery("/api/imports/history/feed-messages", feedMessageHistoryPageSchema, { gridId: "feed-message-history", userId: session.user.id, defaultSort: { key: "received", dir: "desc" }, defaultSize: PAGE_SIZE, params, paging: "cursor", pageFacts: (data) => ({ continuation: data.nextCursor }), sortKeys: ["message", "received", "status"] });
  const rows = grid.data?.items ?? [];
  useEffect(() => setMeta(rowsMeta(rows.length, grid.page)), [rows.length, grid.page, setMeta]);
  useEffect(() => setSort(grid.sort), [grid.sort, setSort]);
  const columns: GridColumn<FeedMessageHistoryRow>[] = [
    { key: "message", label: "Message", width: 18, sort: { key: "message", defaultDir: "asc" }, render: (row) => <><span className="primary">{row.messageReference}</span><span className="sub">{human(row.documentType)}</span></> },
    { key: "received", label: "Received", width: 15, sort: { key: "received", defaultDir: "desc" }, render: (row) => <><span className="primary">{date(row.receivedAt)}</span><span className="sub">{row.callerName}</span></> },
    { key: "document", label: "Document", width: 11, render: (row) => <Link to={`/search?q=${encodeURIComponent(row.documentId)}`}>Shipment →<span className="sub">Line {row.documentLine}</span></Link> },
    { key: "status", label: "Status", width: 10, sort: { key: "status", defaultDir: "asc" }, render: (row) => badge(row.status) },
    { key: "lines", label: "Lines", width: 17, render: (row) => <><span className="primary">{row.shipped} shipped · {row.assetTags} asset tags</span><span className="sub">{row.rejected} rejected</span></> },
    { key: "hash", label: "Payload SHA-256", width: 15, render: (row) => <span className="serial" title={row.payloadHash}>{row.payloadHash.slice(0, 12)}…</span> },
    { key: "job", label: "Import job", width: 10, render: (row) => <Link to={`/imports?job=${row.importJobId}`}>Open job</Link> },
  ];
  return <><div className="card-body p7-history-note">Message bodies are not kept; the payload hash proves what was received.</div><ErrorNote error={grid.error} />
    <DataGrid columns={columns} page={pageOf(rows, grid.page, grid.pageSize, !!grid.data?.nextCursor)} rowId={(row) => row.feedMessageId}
      onPage={grid.onPage} loading={grid.loading} emptyText="No partner feed messages match these filters." gridId="feed-message-history" userId={session.user.id} sort={grid.sort} defaultSort={{ key: "received", dir: "desc" }} onSort={grid.onSort} pageSize={grid.pageSize} defaultPageSize={PAGE_SIZE} onPageSize={grid.onPageSize} /></>;
}

function ComparisonsContent({ find, from, to, status, setMeta, setSort }: ContentProps) {
  const session = useSession();
  const filters = useMemo(() => historyFilters(find, from, to, status), [find, from, to, status]);
  const params = useMemo(() => new URLSearchParams(query(filters)), [filters]);
  const grid = useGridQuery("/api/imports/history/comparisons", comparisonHistoryPageSchema, { gridId: "comparison-history", userId: session.user.id, defaultSort: { key: "ran", dir: "desc" }, defaultSize: PAGE_SIZE, params, paging: "cursor", pageFacts: (data) => ({ continuation: data.nextCursor }), sortKeys: ["file", "ran", "status"] });
  const rows = grid.data?.items ?? [];
  useEffect(() => setMeta(rowsMeta(rows.length, grid.page)), [rows.length, grid.page, setMeta]);
  useEffect(() => setSort(grid.sort), [grid.sort, setSort]);
  const columns: GridColumn<ComparisonHistoryRow>[] = [
    { key: "compared", label: "Compared", width: 38, sort: { key: "file", defaultDir: "asc" }, render: (row) => <><span className="primary"><InputFileLink file={row.firstFile} /> × <InputFileLink file={row.secondFile} /></span><span className="sub serial" title={`${row.firstFile.hash ?? "—"} · ${row.secondFile.hash ?? "—"}`}>{shortHash(row.firstFile.hash)} · {shortHash(row.secondFile.hash)}</span></> },
    { key: "ran", label: "Ran", width: 16, sort: { key: "ran", defaultDir: "desc" }, render: (row) => date(row.ranAt) },
    { key: "status", label: "Status", width: 13, sort: { key: "status", defaultDir: "asc" }, render: (row) => badge(row.status) },
    { key: "result", label: "Result", width: 23, render: (row) => <><span className="primary">{row.applied} applied · {row.skipped} skipped</span><span className="sub">{row.rejected} rejected</span></> },
    { key: "job", label: "Import job", width: 10, render: (row) => <Link to={`/imports?job=${row.jobId}`}>Open job</Link> },
  ];
  return <><ErrorNote error={grid.error} /><DataGrid columns={columns} page={pageOf(rows, grid.page, grid.pageSize, !!grid.data?.nextCursor)} rowId={(row) => row.jobId}
    onPage={grid.onPage} loading={grid.loading} emptyText="No file comparisons match these filters." gridId="comparison-history" userId={session.user.id} sort={grid.sort} defaultSort={{ key: "ran", dir: "desc" }} onSort={grid.onSort} pageSize={grid.pageSize} defaultPageSize={PAGE_SIZE} onPageSize={grid.onPageSize} /></>;
}

export function Component() {
  const session = useSession(); const viewer = session.user.id;
  const [find, setFind] = useState(""); const [from, setFrom] = useState(""); const [to, setTo] = useState("");
  const [fileStatus, setFileStatus] = useState(""); const [feedStatus, setFeedStatus] = useState(""); const [comparisonStatus, setComparisonStatus] = useState("");
  const [uploader, setUploader] = useState(""); const [uploaders, setUploaders] = useState<Array<[string, string]>>([]);
  const [fileMeta, setFileMeta] = useState("Rows 0–0"); const [feedMeta, setFeedMeta] = useState("Rows 0–0"); const [comparisonMeta, setComparisonMeta] = useState("Rows 0–0");
  const [fileSort, setFileSort] = useState<GridSort>({ key: "uploaded", dir: "desc" });
  const [feedSort, setFeedSort] = useState<GridSort>({ key: "received", dir: "desc" });
  const [comparisonSort, setComparisonSort] = useState<GridSort>({ key: "ran", dir: "desc" });
  const shared = { find, from, to };
  return <main className="content" id="main" tabIndex={-1}><Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Imports", to: "/imports" }, { label: "Upload history" }]} />
    <PageHeader eyebrow="Data · Imports" title="Upload history" sub="Every file, partner message and comparison that came in, with its integrity hash." actions={<Link className="btn" to="/imports">Back to imports</Link>} />
    <section className="card p7-history-shared"><FindBar query={find} onQuery={setFind} facets={[]} activeFacet={null} onFacet={() => undefined}
      ariaLabel="Find by name, message reference or SHA-256 hash" placeholder="Find by name, message reference or SHA-256 hash" status="Applies to all cards" />
      <div className="p7-history-dates"><label>From<input className="input" type="date" value={from} onChange={(event) => setFrom(event.target.value)} /></label><label>To<input className="input" type="date" value={to} onChange={(event) => setTo(event.target.value)} /></label></div></section>
    <div className="stack top">
      <CollapsibleCard title="Uploaded files" meta={fileMeta} storageKey={`upload-history:${viewer}:files`} headerActions={<>
        <StatusSelect value={fileStatus} onChange={setFileStatus} values={JOB_STATUSES} />
        <label className="p7-card-filter">Uploader<select className="select" value={uploader} onChange={(event) => setUploader(event.target.value)}><option value="">All uploaders</option>{uploaders.map(([id, name]) => <option value={id} key={id}>{name}</option>)}</select></label>
        <ExportCsv path={`/api/imports/history/export${query({ ...historyFilters(find, from, to, fileStatus, uploader), sort: fileSort.key, dir: fileSort.dir })}`} /></>}>
        <UploadedFilesContent {...shared} status={fileStatus} uploader={uploader} setMeta={setFileMeta} setSort={setFileSort} setUploaders={setUploaders} />
      </CollapsibleCard>
      <CollapsibleCard title="Partner feed messages" meta={feedMeta} storageKey={`upload-history:${viewer}:feed-messages`} headerActions={<>
        <StatusSelect value={feedStatus} onChange={setFeedStatus} values={FEED_STATUSES} />
        <ExportCsv path={`/api/imports/history/feed-messages/export${query({ ...historyFilters(find, from, to, feedStatus), sort: feedSort.key, dir: feedSort.dir })}`} /></>}>
        <FeedMessagesContent {...shared} status={feedStatus} setMeta={setFeedMeta} setSort={setFeedSort} />
      </CollapsibleCard>
      <CollapsibleCard title="File comparisons" meta={comparisonMeta} storageKey={`upload-history:${viewer}:comparisons`} headerActions={<>
        <StatusSelect value={comparisonStatus} onChange={setComparisonStatus} values={JOB_STATUSES} />
        <ExportCsv path={`/api/imports/history/comparisons/export${query({ ...historyFilters(find, from, to, comparisonStatus), sort: comparisonSort.key, dir: comparisonSort.dir })}`} /></>}>
        <ComparisonsContent {...shared} status={comparisonStatus} setMeta={setComparisonMeta} setSort={setComparisonSort} />
      </CollapsibleCard>
    </div>
  </main>;
}

/** A comparison input: a link to its job when the file is still identifiable, else its name as plain text. */
function InputFileLink({ file }: { file: ComparisonInputFile }) {
  return file.id ? <Link to={`/imports?job=${file.id}`}>{file.name}</Link> : <span className="muted">{file.name}</span>;
}

function shortHash(hash: string | null): string { return hash ? `${hash.slice(0, 8)}…` : "—"; }
