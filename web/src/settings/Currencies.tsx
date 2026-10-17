import { useEffect, useMemo, useState } from "react";
import { Link } from "react-router";
import { useSession } from "../session/session";
import { Icon } from "../shell/Icons";
import { Breadcrumbs } from "../shell/PageHeader";
import { ErrorNote } from "../units/common";
import { claimsApi } from "../claims/api";
import type { CurrencyInfo } from "../claims/types";
import "../claims/claims.css";

export function Component() {
  const session = useSession();
  const permitted = session.roles.includes("tenant_administrator");
  const [catalogue, setCatalogue] = useState<CurrencyInfo[]>([]);
  const [working, setWorking] = useState("");
  const [accepted, setAccepted] = useState<Set<string>>(new Set());
  const [workingSearch, setWorkingSearch] = useState("");
  const [acceptedSearch, setAcceptedSearch] = useState("");
  const [loading, setLoading] = useState(permitted);
  const [busy, setBusy] = useState(false);
  const [saved, setSaved] = useState(false);
  const [error, setError] = useState<unknown>(null);

  useEffect(() => {
    if (!permitted) return;
    const controller = new AbortController();
    Promise.all([claimsApi.currency(controller.signal), claimsApi.currencyCatalogue(controller.signal)])
      .then(([settings, nextCatalogue]) => { setCatalogue(nextCatalogue); setWorking(settings.currency ?? ""); setAccepted(new Set(settings.accepted.filter((code) => code !== settings.currency))); })
      .catch((reason) => { if (!(reason instanceof DOMException && reason.name === "AbortError")) setError(reason); })
      .finally(() => setLoading(false));
    return () => controller.abort();
  }, [permitted]);

  const workingChoices = useMemo(() => filterCurrencies(catalogue, workingSearch, working), [catalogue, workingSearch, working]);
  const acceptedChoices = useMemo(() => filterCurrencies(catalogue.filter((currency) => currency.code !== working), acceptedSearch), [catalogue, acceptedSearch, working]);
  const toggle = (code: string) => setAccepted((current) => { const next = new Set(current); if (next.has(code)) next.delete(code); else next.add(code); return next; });
  const save = async () => {
    setBusy(true); setSaved(false); setError(null);
    try { const result = await claimsApi.saveCurrency(working, [...accepted].filter((code) => code !== working).sort()); setWorking(result.currency ?? working); setAccepted(new Set(result.accepted.filter((code) => code !== result.currency))); setSaved(true); }
    catch (reason) { setError(reason); } finally { setBusy(false); }
  };

  if (!permitted) return <main className="content" id="main" tabIndex={-1}><Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Settings", to: "/settings" }, { label: "Currencies" }]} /><div className="page-header"><div><div className="eyebrow">Settings · tenant administrator</div><h1>Currencies</h1></div></div><div className="note bad"><strong>Tenant administrator access is required.</strong>&nbsp;This setting changes the currency context for configured monetary values.</div></main>;

  return <main className="content" id="main" tabIndex={-1}>
    <Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Settings", to: "/settings" }, { label: "Currencies" }]} />
    <div className="page-header"><div><div className="eyebrow">Settings · tenant administrator</div><h1>Currencies</h1><p className="sub">Choose the tenant’s working currency and the other currencies claims, documents and commercial records may use.</p></div><div className="actions"><Link className="btn" to="/settings">Cancel</Link><button type="button" className="btn primary" disabled={loading || busy || !working} aria-busy={busy} onClick={() => void save()}><Icon name="check" />Save</button></div></div>
    <ErrorNote error={error} />
    {saved && <div className="note ok" style={{ marginBottom: 16 }}><strong>Currency settings saved.</strong>&nbsp;New forms now use this working and accepted currency list.</div>}
    <div className="note warn" style={{ marginBottom: 16 }}><strong>Changing the working currency does not convert values.</strong>&nbsp;Every configured amount—including approval thresholds and policy values—is expressed in the working currency. Review those values separately after any change.</div>
    <div className="cols-3-2">
      <section className="card" aria-labelledby="working-currency"><div className="card-header"><h2 id="working-currency">Working currency</h2><span className="meta">home currency · exactly one</span></div><div className="card-body stack">
        <div className="field"><label htmlFor="working-currency-search">Find a currency</label><input id="working-currency-search" className="input" type="search" value={workingSearch} onChange={(event) => setWorkingSearch(event.target.value)} placeholder="Code or currency name" /></div>
        <div className="field"><label htmlFor="working-currency-select">Working currency</label><select id="working-currency-select" className="select" size={Math.min(12, Math.max(2, workingChoices.length))} value={working} onChange={(event) => { setWorking(event.target.value); setAccepted((current) => { const next = new Set(current); next.delete(event.target.value); return next; }); }}>{workingChoices.map((currency) => <option key={currency.code} value={currency.code}>{currency.code} · {currency.name} · {currency.symbol}</option>)}</select><span className="help">All configured monetary thresholds and policy values are interpreted in this currency.</span></div>
      </div></section>
      <section className="card" aria-labelledby="accepted-currencies"><div className="card-header"><h2 id="accepted-currencies">Accepted currencies <span className="meta">{accepted.size} additional</span></h2></div><div className="card-body stack">
        <div className="field"><label htmlFor="accepted-currency-search">Find accepted currencies</label><input id="accepted-currency-search" className="input" type="search" value={acceptedSearch} onChange={(event) => setAcceptedSearch(event.target.value)} placeholder="Code or currency name" /></div>
        <div className="claims-chip-list" aria-label="Accepted currency selection">{[...accepted].sort().map((code) => <button type="button" className="chip on" key={code} onClick={() => toggle(code)}>{code}<Icon name="x" /></button>)}</div>
        <div className="claims-currency-list">{acceptedChoices.map((currency) => <label className="claims-currency-option" key={currency.code}><input type="checkbox" checked={accepted.has(currency.code)} onChange={() => toggle(currency.code)} /><span><strong>{currency.code} · {currency.name}</strong><span className="sub">{currency.symbol} · {currency.minorDigits} minor digits</span></span></label>)}</div>
        {!acceptedChoices.length && <p className="muted">No currencies match this search.</p>}
      </div></section>
    </div>
  </main>;
}

function filterCurrencies(catalogue: readonly CurrencyInfo[], query: string, include = ""): CurrencyInfo[] {
  const text = query.trim().toLowerCase();
  if (!text) return [...catalogue];
  return catalogue.filter((currency) => currency.code === include || currency.code.toLowerCase().includes(text) || currency.name.toLowerCase().includes(text));
}
