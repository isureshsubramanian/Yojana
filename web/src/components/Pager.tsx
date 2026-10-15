import { pageCount } from "./paging";

export interface PagerProps {
  page: number;
  pageSize: number;
  total: number;
  onPage: (page: number) => void;
  /** Hidden when the server pages by continuation and the page count is unknown. */
  showPageCount?: boolean;
}

export function Pager({ page, pageSize, total, onPage, showPageCount = true }: PagerProps) {
  const pages = pageCount(total, pageSize);
  return (
    <span className="pager" role="navigation" aria-label="Pages">
      <button type="button" className="btn" disabled={page <= 1} onClick={() => onPage(page - 1)}>Previous</button>
      {showPageCount && <span className="muted" style={{ padding: "0 8px" }}>Page {page.toLocaleString()} of {pages.toLocaleString()}</span>}
      <button type="button" className="btn" disabled={page >= pages} onClick={() => onPage(page + 1)}>Next</button>
    </span>
  );
}
