import { useState } from "react";
import { Link } from "react-router";
import { FormActions, StatusBadge } from "../components";
import { CustomerField } from "../corrections/ReferenceFields";
import { useSession } from "../session/session";
import { Icon } from "../shell/Icons";
import { PageHeader } from "../shell/PageHeader";
import { ErrorNote } from "../units/common";
import { ScopeChoices, SecretValue } from "./DeveloperClients";
import { developerApi, type ApiClientCreated, type DisclosureTier } from "./developers/developerApi";
import "./developers/developer.css";

const CLIENTS = "/settings/developers/clients";

export function Component() {
  const permitted = useSession().roles.includes("tenant_administrator");
  const [name, setName] = useState("");
  const [tier, setTier] = useState<DisclosureTier>("customer");
  const [customer, setCustomer] = useState<string | null>(null);
  const [rate, setRate] = useState(600);
  const [expires, setExpires] = useState("");
  const [scopes, setScopes] = useState<string[]>(["units.read", "cover.read", "certificates.read"]);
  const [sandbox, setSandbox] = useState(false);
  const [created, setCreated] = useState<ApiClientCreated | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const ready = name.trim().length > 0 && scopes.length > 0 && rate >= 1 && rate <= 60_000
    && (tier !== "customer" || customer !== null);
  const create = async () => {
    if (!ready) return;
    setBusy(true); setError(null);
    try { setCreated(await developerApi.createClient({ name: name.trim(), disclosureTier: tier, customerPartyId: customer,
      rateLimitPerMinute: rate, expiresAt: expires || null, scopes, sandbox })); }
    catch (reason) { setError(reason); }
    finally { setBusy(false); }
  };
  return <main className="content" id="main" tabIndex={-1}>
    <PageHeader eyebrow="Settings · Developers · API clients" title="New API client" sub="Give the application only the scopes it needs. You can widen them later." />
    {!permitted ? <div className="note bad"><strong>Tenant administrator access is required.</strong></div> : <div className="developer-split">
      <section className="card" aria-labelledby="new-client"><div className="card-header"><h2 id="new-client">Client</h2></div><div className="card-body stack">
        <ErrorNote error={error} />
        <div className="form-grid">
          <label className="field span"><span className="lbl">Name</span><input className="input" maxLength={120} value={name} onChange={event => setName(event.target.value)} /><span className="help">Shown in the audit trail and the security log.</span></label>
          <div className="field"><span className="lbl">Disclosure tier</span><div className="segmented plain" role="group" aria-label="Disclosure tier"><button type="button" aria-pressed={tier === "customer"} onClick={() => setTier("customer")}>Customer</button><button type="button" aria-pressed={tier === "back_office"} onClick={() => setTier("back_office")}>Back office</button></div><span className="help">Customer tier shows only the fields your disclosure settings allow.</span></div>
          <CustomerField id="new-client-customer" label="Limit to one customer" optional={tier !== "customer"} value={customer} onChange={setCustomer} hint={tier === "customer" ? "Required for the customer disclosure tier." : "Leave empty to see every customer."} />
          <label className="field"><span className="lbl">Rate limit</span><input className="input num" type="number" min={1} max={60000} value={rate} onChange={event => setRate(Number(event.target.value))} /><span className="help">Requests a minute. Above it the application gets 429 with a retry time.</span></label>
          <label className="field"><span className="lbl">Expires <span className="hint">· optional</span></span><input className="input" type="date" value={expires} onChange={event => setExpires(event.target.value)} /><span className="help">Leave empty for no expiry.</span></label>
          <div className="field span"><span className="lbl">Scopes</span><ScopeChoices selected={scopes} onChange={setScopes} />{scopes.length === 0 && <span className="help bad" role="alert">Choose at least one scope.</span>}</div>
          <label className="checkrow span"><input type="checkbox" checked={sandbox} onChange={event => setSandbox(event.target.checked)} /><span><strong>Sandbox client</strong>: receives only test events, for building and testing an application.</span></label>
        </div>
        <FormActions status={created ? "Client created." : undefined}><Link className="btn" to={CLIENTS}>Cancel</Link><button type="button" className="btn primary" disabled={!ready || busy || created !== null} onClick={() => void create()}><Icon name="plus" />Create client</button></FormActions>
      </div></section>
      <section className="card developer-once" aria-labelledby="client-secret"><div className="card-header"><h2 id="client-secret">After you create it</h2><StatusBadge tone="warn">Shown once</StatusBadge></div><div className="card-body stack">
        {created ? <><p>Copy the secret into the application's configuration now. Yojana keeps only a fingerprint and cannot show it again.</p><div className="field"><span className="lbl">Client id</span><SecretValue value={created.client.id} label="Copy the client id" /></div><div className="field"><span className="lbl">Secret</span><SecretValue value={created.secret} label="Copy the client secret" /></div><p className="help">Next: exchange these for a one-hour token at <code>POST /api/public/v1/token</code>. See Developer documentation › Authentication.</p><Link className="btn" to="/settings/developers/docs">Open documentation</Link></>
          : <p className="muted">The client id and secret appear here once the client is created.</p>}
      </div></section>
    </div>}
  </main>;
}
