import { useEffect, useMemo, useState } from "react";
import { Link } from "react-router";
import { useSession } from "../session/session";
import { Icon } from "../shell/Icons";
import { Breadcrumbs } from "../shell/PageHeader";
import { ErrorNote } from "../units/common";
import { claimsApi } from "../claims/api";
import type { CurrencyInfo, CurrencyUsageReport, TenantCurrency } from "../claims/types";
import "../claims/claims.css";

export function Component() {
  const session = useSession();
  const permitted = session.roles.includes("tenant_administrator");
  const [catalogue, setCatalogue] = useState<CurrencyInfo[]>([]);
  const [status, setStatus] = useState<TenantCurrency>({ currency: null, changeable: true, usage: [] });
  const [report, setReport] = useState<CurrencyUsageReport>({ currency: null, rows: [] });
  const [home, setHome] = useState("");
  const [search, setSearch] = useState("");
  const [loading, setLoading] = useState(permitted);
  const [busy, setBusy] = useState(false);
  const [saved, setSaved] = useState(false);
  const [error, setError] = useState<unknown>(null);

  useEffect(() => {
    if (!permitted) return;
    const controller = new AbortController();
    Promise.all([claimsApi.currency(controller.signal), claimsApi.currencyCatalogue(controller.signal), claimsApi.currencyReport(controller.signal)])
      .then(([nextStatus, nextCatalogue, nextReport]) => { setStatus(nextStatus); setCatalogue(nextCatalogue); setReport(nextReport); setHome(nextStatus.currency ?? ""); })
      .catch((reason) => { if (!(reason instanceof DOMException && reason.name === "AbortError")) setError(reason); })
      .finally(() => setLoading(false));
    return () => controller.abort();
  }, [permitted]);

  const choices = useMemo(() => filterCurrencies(catalogue, search, home), [catalogue, search, home]);
  const save = async () => {
    setBusy(true); setSaved(false); setError(null);
    try { const result = await claimsApi.saveCurrency(home); setStatus(result); setHome(result.currency ?? home); setSaved(true); }
    catch (reason) { setError(reason); } finally { setBusy(false); }
  };

  if (!permitted) return <main className="content" id="main" tabIndex={-1}><Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Settings", to: "/settings" }, { label: "Currencies" }]} /><div className="page-header"><div><div className="eyebrow">Settings · tenant administrator</div><h1>Currencies</h1></div></div><div className="note bad"><strong>Tenant administrator access is required.</strong>&nbsp;This setting changes the currency context for company amounts.</div></main>;

  const lockedReason = status.usage.map((row) => `${row.count.toLocaleString()} ${row.kind}`).join(" · ");
  return <main className="content" id="main" tabIndex={-1}>
    <Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Settings", to: "/settings" }, { label: "Currencies" }]} />
    <div className="page-header"><div><div className="eyebrow">Settings · tenant administrator</div><h1>Currencies</h1><p className="sub">The company records every amount in one home currency. It can change only while no amount or money threshold is recorded.</p></div><div className="actions"><Link className="btn" to="/settings">Cancel</Link><button type="button" className="btn primary" disabled={loading || busy || !home || !status.changeable} aria-busy={busy} onClick={() => void save()}><Icon name="check" />Save</button></div></div>
    <ErrorNote error={error} />
    {saved && <div className="note ok" style={{ marginBottom: 16 }}><strong>Home currency saved.</strong>&nbsp;New amounts use {status.currency}.</div>}
    {!loading && status.changeable && <div className="note warn" style={{ marginBottom: 16 }}><strong>Changeable until the first amount.</strong>&nbsp;Claims, prices, billing, costs and money thresholds lock the home currency. Amounts are never converted.</div>}
    {!loading && !status.changeable && <div className="note" style={{ marginBottom: 16 }}><strong>Locked: {lockedReason} {status.currency ? `are recorded in ${status.currency}` : "are recorded"}.</strong>&nbsp;Amounts are never converted.</div>}
    <section className="card" aria-labelledby="home-currency"><div className="card-header"><h2 id="home-currency">Home currency</h2><span className="meta">{status.changeable ? "changeable" : "locked"}</span></div><div className="card-body stack">
      {!status.changeable ? <div className="field"><span className="label">Home currency</span><strong>{describe(catalogue, home)}</strong><span className="help">Recorded amounts give this code its meaning, so changing the label is refused.</span></div> : <>
        <div className="field"><label htmlFor="home-currency-search">Find a currency</label><input id="home-currency-search" className="input" type="search" value={search} onChange={(event) => setSearch(event.target.value)} placeholder="Code or currency name" /></div>
        <div className="field"><label htmlFor="home-currency-select">Home currency</label><select id="home-currency-select" className="select" value={home} onChange={(event) => setHome(event.target.value)}><option value="">Choose…</option>{choices.map((currency) => <option key={currency.code} value={currency.code}>{currency.code} · {currency.name} · {`\u2068${currency.symbol}\u2069`}</option>)}</select><span className="help">All recorded amounts, configured money thresholds and policy values use this currency.</span></div>
      </>}
    </div></section>
    <section className="card" id="currency-report" aria-labelledby="currency-report-title" style={{ marginTop: 16 }}><div className="card-header"><h2 id="currency-report-title">Amounts recorded before this rule</h2><span className="meta">read-only upgrade report</span></div><div className="card-body">
      {report.rows.length === 0 ? <p className="muted">No rows are stored in another currency.</p> : <><div className="note warn"><strong>These rows are not converted or silently included in totals.</strong>&nbsp;Review them as historical data.</div><table className="table"><thead><tr><th>Record kind</th><th>Stored currency</th><th className="num">Rows</th></tr></thead><tbody>{report.rows.map((row) => <tr key={`${row.kind}:${row.currency}`}><td>{row.kind}</td><td>{row.currency}</td><td className="num">{row.count.toLocaleString()}</td></tr>)}</tbody></table></>}
    </div></section>
  </main>;
}

function describe(catalogue: readonly CurrencyInfo[], code: string): string {
  const currency = catalogue.find((row) => row.code === code);
  return currency ? `${currency.code} · ${currency.name} · \u2068${currency.symbol}\u2069` : code;
}

function filterCurrencies(catalogue: readonly CurrencyInfo[], query: string, include = ""): CurrencyInfo[] {
  const text = query.trim().toLowerCase();
  if (!text) return [...catalogue];
  return catalogue.filter((currency) => currency.code === include || currency.code.toLowerCase().includes(text) || currency.name.toLowerCase().includes(text));
}
