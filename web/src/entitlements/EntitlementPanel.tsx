import { useEffect, useMemo, useState } from "react";
import { Link } from "react-router";
import { StatusBadge } from "../components";
import { openDrawer } from "../shell/drawerStore";
import { Kv } from "../units/common";
import { fmtDate, humanise, shortId } from "../units/format";
import { entitlementApi } from "./api";
import { entitlementTone, groupEntitlements } from "./format";
import type { EntitlementView, WarrantyCheck } from "./types";

export function EntitlementPanel({ unitId }: { unitId: string }) {
  const [rows, setRows] = useState<EntitlementView[]>([]);
  const [check, setCheck] = useState<WarrantyCheck | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<unknown>(null);
  const [active, setActive] = useState<string | null>(null);

  useEffect(() => {
    const controller = new AbortController();
    setLoading(true); setError(null);
    Promise.all([entitlementApi.forUnit(unitId, controller.signal), entitlementApi.warrantyCheck(unitId, controller.signal)])
      .then(([entitlements, warranty]) => { setRows(entitlements); setCheck(warranty); setLoading(false); })
      .catch((reason) => { if (!(reason instanceof DOMException && reason.name === "AbortError")) { setError(reason); setLoading(false); } });
    return () => controller.abort();
  }, [unitId]);

  const grouped = useMemo(() => groupEntitlements(rows), [rows]);
  const show = (row: EntitlementView) => {
    setActive(row.entitlementId);
    openDrawer({ eyebrow: `Selected entitlement · ${row.coverageFamily}`, title: shortId(row.entitlementId, 12), mono: true }, (
      <>
        <h3>Entitlement</h3>
        <Kv rows={[["Status", humanise(row.effectiveStatus)], ["Stored status", humanise(row.storedStatus)], ["Direction · type", `${humanise(row.direction)} · ${humanise(row.type)}`], ["Start", `${fmtDate(row.startDate)}${row.startBasis ? ` · ${humanise(row.startBasis)}` : ""}`], ["End", fmtDate(row.endDate)], ["Days remaining", row.daysRemaining?.toLocaleString() ?? "—"], ["Reference zone", row.startTimeZone], ["Policy version", shortId(row.policyVersionId, 12)], ["Root", shortId(row.rootEntitlementId, 12)], ["Parent", shortId(row.parentEntitlementId, 12)], ["Supersedes", shortId(row.supersedesEntitlementId, 12)], ["Closed reason", humanise(row.closedReason) || "—"]]} />
        <div className="act"><Link className="btn primary block" to={`/entitlements/bulk?operation=entitlement.amend&serials=${encodeURIComponent(unitId)}&family=${encodeURIComponent(row.coverageFamily)}`}>Amend through preview…</Link></div>
      </>
    ));
  };

  return (
    <section className="card entitlement-panel" aria-labelledby="h-entitlements" style={{ marginBottom: "var(--gap)" }}>
      <div className="card-header">
        <h2 id="h-entitlements">Entitlements <span className="meta">one live per coverage family · stacked</span></h2>
        {check && <StatusBadge tone={check.covered ? "ok" : "bad"}>{check.covered ? `Covered to ${fmtDate(check.endDate)}` : `Not covered · ${humanise(check.reason)}`}</StatusBadge>}
      </div>
      {error ? <div className="empty"><h2>Entitlements could not be loaded</h2><p className="hint">{error instanceof Error ? error.message : String(error)}</p></div> : loading ? <div className="empty"><p className="hint">Loading entitlements…</p></div> : rows.length === 0 ? <div className="empty"><h2>No entitlements recorded</h2><p className="hint">If this unit has been sold, check the gap report or its entitlement requirement.</p><Link className="btn" to="/entitlements/gaps">Open gap report</Link></div> : (
        <>
          <table className="table">
            <colgroup><col style={{ width: "21%" }} /><col style={{ width: "15%" }} /><col style={{ width: "18%" }} /><col style={{ width: "18%" }} /><col style={{ width: "12%" }} /><col style={{ width: "16%" }} /></colgroup>
            <thead><tr><th>Coverage family</th><th>Status</th><th>Start · basis</th><th>End</th><th className="num">Days left</th><th>Lineage</th></tr></thead>
            <tbody>{[...grouped.live, ...grouped.history].map((row) => <tr key={row.entitlementId} className={`clickable${active === row.entitlementId ? " sel" : ""}`} tabIndex={0} onKeyDown={(event) => { if (event.key === "Enter" || event.key === " ") { event.preventDefault(); show(row); } }} onClick={() => show(row)}>
              <td className="primary">{row.coverageFamily}<span className="sub">{humanise(row.type)} · {humanise(row.direction)}</span></td>
              <td><StatusBadge tone={entitlementTone(row.effectiveStatus)}>{humanise(row.effectiveStatus)}</StatusBadge>{row.closedReason && <span className="sub">{humanise(row.closedReason)}</span>}</td>
              <td>{fmtDate(row.startDate)}<span className="sub">{humanise(row.startBasis) || "pending evidence"}</span></td>
              <td>{fmtDate(row.endDate)}<span className="sub">{row.startTimeZone}</span></td>
              <td className="num">{row.daysRemaining?.toLocaleString() ?? "—"}</td>
              <td className="serial">{shortId(row.entitlementId)}<span className="sub" style={{ fontFamily: "var(--font-body)" }}>{row.supersedesEntitlementId ? `supersedes ${shortId(row.supersedesEntitlementId)}` : row.parentEntitlementId ? `child of ${shortId(row.parentEntitlementId)}` : "root"}</span></td>
            </tr>)}</tbody>
          </table>
          <div className="card-footer"><span>{grouped.live.length} live · {grouped.history.length} closed or expired · end dates are policy outputs</span><Link to={`/entitlements/bulk?serials=${encodeURIComponent(unitId)}`}>Bulk operation</Link></div>
        </>
      )}
    </section>
  );
}
