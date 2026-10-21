import { useEffect, useState } from "react";
import { Link } from "react-router";
import { StatusBadge } from "../components/StatusBadge";
import { useTenantTime } from "../components/tenantTime";
import { useSession } from "../session/session";
import { mailApi, type SentMailPage } from "./api";
import { SENDER_ROLES, deliveryState, holds } from "./format";

/**
 * The last customer e-mails about one unit, beside its history. Sent mail is not a unit event, so it stays out of the
 * history rail and comes from the sent log; the card shows only to people who may read that log.
 */
export function UnitCustomerMail({ unitId }: { unitId: string }) {
  const permitted = holds(useSession().roles, SENDER_ROLES);
  const tenantTime = useTenantTime();
  const [page, setPage] = useState<SentMailPage | null>(null);
  useEffect(() => {
    if (!permitted) return;
    const controller = new AbortController();
    // A failure here only hides the card; the unit page itself does not depend on it.
    mailApi.aboutUnit(unitId, controller.signal).then(setPage).catch(() => undefined);
    return () => controller.abort();
  }, [permitted, unitId]);
  if (!permitted || !page || page.total === 0) return null;
  return <section className="card unit-customer-mail" aria-labelledby="h-mail">
    <div className="card-header"><h2 id="h-mail">Customer e-mail <span className="meta">{page.total.toLocaleString()} sent</span></h2><Link className="btn ghost sm" to={`/mail/sent?unitId=${unitId}`}>Sent log</Link></div>
    <div className="card-body">
      {page.rows.map((row) => { const state = deliveryState(row); return <div className="job" key={row.messageId}>
        <div><div className="t">{row.toName ?? row.to}{row.ccCount ? ` +${row.ccCount} in Cc` : ""}</div><div className="s">{tenantTime.dateTime(row.queuedAt)} · {row.templateName} v{row.templateVersion} · {row.automatic ? "sent automatically" : row.sentBy}</div></div>
        <StatusBadge tone={state.tone}>{state.text}</StatusBadge>
      </div>; })}
    </div>
  </section>;
}
