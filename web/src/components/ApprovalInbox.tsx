import { Link } from "react-router";
import { StatusBadge } from "./StatusBadge";
import type { ApprovalItem } from "./types";

export interface ApprovalInboxProps {
  items: ApprovalItem[];
  total: number;
  title?: string;
  viewAllHref?: string | undefined;
  emptyText?: string;
}

/** The approver sees the preview, not a form: this list is the way in. Rows link to the command's own preview. */
export function ApprovalInbox({ items, total, title = "Approval inbox", viewAllHref, emptyText = "Nothing is waiting for approval." }: ApprovalInboxProps) {
  return (
    <section className="card" aria-labelledby="h-inbox">
      <div className="card-header">
        <h2 id="h-inbox">{title} <span className="meta">{total.toLocaleString()} waiting</span></h2>
        {viewAllHref && <Link to={viewAllHref} className="btn ghost sm">View all</Link>}
      </div>
      {items.length === 0 ? (
        <div className="empty"><p className="hint">{emptyText}</p></div>
      ) : (
        <table className="table">
          <colgroup><col style={{ width: "33%" }} /><col style={{ width: "27%" }} /><col style={{ width: "12%" }} /><col style={{ width: "28%" }} /></colgroup>
          <thead><tr><th>Command</th><th>Selection set</th><th className="num">Units</th><th>Status</th></tr></thead>
          <tbody>
            {items.map((i) => (
              <tr key={i.id}>
                <td className="primary"><Link to={i.href}>{i.command}</Link><span className="sub">{i.initiator} · {i.requestedAt}</span></td>
                <td className={i.selectionMono ? "serial" : undefined} title={i.selection}>{i.selection}{i.selectionSub && <span className="sub" style={i.selectionMono ? { fontFamily: "var(--font-body)" } : undefined}>{i.selectionSub}</span>}</td>
                <td className="num">{i.units.toLocaleString()}</td>
                <td><StatusBadge tone={i.tone}>{i.status}</StatusBadge></td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </section>
  );
}
