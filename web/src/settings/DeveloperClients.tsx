import { useCallback, useEffect, useMemo, useState } from "react";
import { Link } from "react-router";
import { ActionDialog, DataGrid, KpiStrip, StatusBadge, type GridColumn, type PageResult } from "../components";
import { useSession } from "../session/session";
import { Icon } from "../shell/Icons";
import { openDrawer, updateDrawer } from "../shell/drawerStore";
import { PageHeader } from "../shell/PageHeader";
import { ErrorNote, Kv, Section } from "../units/common";
import { developerApi, type ApiClientPage, type ApiClientView } from "./developers/developerApi";
import { clientSees, clientStatus, relative, scopeDescription, shortClientId, when } from "./developers/developerModel";
import "./developers/developer.css";

const PAGE_SIZE = 50;

export function Component() {
  const permitted = useSession().roles.includes("tenant_administrator");
  const [result, setResult] = useState<ApiClientPage | null>(null);
  const [page, setPage] = useState(1);
  const [cursors, setCursors] = useState<(string | undefined)[]>([undefined]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<unknown>(null);

  const load = useCallback(async (target = page, cursor = cursors[target - 1]) => {
    setLoading(true); setError(null);
    try {
      const next = await developerApi.clients(cursor);
      setResult(next); setPage(target);
      if (next.next) setCursors(current => { const copy = current.slice(0, target); copy[target] = next.next!; return copy; });
    } catch (reason) { setError(reason); }
    finally { setLoading(false); }
  }, [cursors, page]);
  useEffect(() => { if (permitted) void load(1, undefined); }, [permitted]); // eslint-disable-line react-hooks/exhaustive-deps

  const show = useCallback((row: ApiClientView) => {
    const head = { eyebrow: "API client", title: row.name };
    const changed = (next: ApiClientView, secret?: string) => { updateDrawer(head, <ClientDrawer client={next} initialSecret={secret} onChanged={changed} />); void load(); };
    openDrawer(head, <ClientDrawer client={row} onChanged={changed} />);
  }, [load]);
  const columns = useMemo<GridColumn<ApiClientView>[]>(() => [
    { key: "name", label: "Name", width: 25, render: row => <span><span className="primary">{row.name}</span><span className="sub mono">{shortClientId(row.id)}</span></span> },
    { key: "scopes", label: "Scopes", width: 27, render: row => row.scopes.map(scope => scope.split(".")[0]).filter((value, index, all) => all.indexOf(value) === index).join(", ") },
    { key: "sees", label: "Sees", width: 22, render: clientSees },
    { key: "used", label: "Last used", width: 13, render: row => relative(row.lastUsedAt) },
    { key: "status", label: "Status", width: 13, render: row => { const status = clientStatus(row); return <StatusBadge tone={status.tone}>{status.label}</StatusBadge>; } },
  ], []);
  const rows = result?.items ?? [];
  const grid: PageResult<ApiClientView> = { rows, page, pageSize: PAGE_SIZE,
    total: (page - 1) * PAGE_SIZE + rows.length + (result?.next ? 1 : 0), continuation: result?.next ?? null,
    totalIsExact: false, hasMore: !!result?.next };

  return <main className="content" id="main" tabIndex={-1}>
    <PageHeader eyebrow="Settings · Developers · tenant administrators only" title="API clients"
      sub="Your own applications read Yojana data through the public API with a client id and secret. Each client sees only the data its scopes and disclosure tier allow."
      actions={permitted && <><Link className="btn" to="/settings/developers/docs"><Icon name="book" />Documentation</Link><Link className="btn primary" to="/settings/developers/clients/new"><Icon name="plus" />New client</Link></>} />
    {!permitted ? <div className="note bad"><strong>Tenant administrator access is required.</strong></div> : <div className="stack">
      <ErrorNote error={error} onReload={() => void load()} />
      <KpiStrip label="API client figures" kpis={[
        { label: "Active clients", value: result?.summary.active.toLocaleString() ?? "—", detail: `${result?.summary.sandbox ?? 0} sandbox` },
        { label: "Last used", value: result?.summary.lastUsedAt ? relative(result.summary.lastUsedAt) : "Never", detail: "most recent client use" },
        { label: "Revoked clients", value: result?.summary.revoked.toLocaleString() ?? "—", detail: "tokens stopped immediately" },
        { label: "Secrets older than 1 year", value: result?.summary.secretsOlderThanOneYear.toLocaleString() ?? "—", detail: "rotate them soon" },
      ]} />
      <section className="card" aria-labelledby="developer-client-list"><div className="card-header"><h2 id="developer-client-list">Clients</h2><span className="meta">active first</span></div>
        <DataGrid columns={columns} page={grid} rowId={row => row.id} onPage={target => { if (target < page || result?.next) void load(target, cursors[target - 1]); }} onRowClick={show} loading={loading} emptyText="No API clients have been created." viewportHeight={560} />
      </section>
    </div>}
  </main>;
}

function ClientDrawer({ client, initialSecret = null, onChanged }: { client: ApiClientView; initialSecret?: string | null | undefined; onChanged: (client: ApiClientView, secret?: string) => void }) {
  const [mode, setMode] = useState<"edit" | "rotate" | "revoke" | null>(null);
  const [scopes, setScopes] = useState(client.scopes);
  const [rate, setRate] = useState(client.rateLimitPerMinute);
  const [secret, setSecret] = useState<string | null>(initialSecret);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const status = clientStatus(client);
  const copy = (value: string) => void navigator.clipboard.writeText(value);
  const save = async () => { setBusy(true); setError(null); try { onChanged(await developerApi.updateClient(client.id, { scopes, rateLimitPerMinute: rate })); setMode(null); } catch (reason) { setError(reason); } finally { setBusy(false); } };
  const rotate = async () => { setBusy(true); setError(null); try { const result = await developerApi.rotateClient(client.id); setSecret(result.secret); setMode(null); onChanged({ ...client, secretRotatedAt: new Date().toISOString() }, result.secret); } catch (reason) { setError(reason); } finally { setBusy(false); } };
  const revoke = async () => { setBusy(true); setError(null); try { onChanged(await developerApi.revokeClient(client.id)); setMode(null); } catch (reason) { setError(reason); } finally { setBusy(false); } };
  return <>
    <ErrorNote error={error} />
    <Section title="Client"><Kv rows={[["Status", <StatusBadge key="status" tone={status.tone}>{status.label}</StatusBadge>], ["Client id", <span className="developer-copy" key="id"><span className="mono">{client.id}</span><button type="button" className="icon-btn" title="Copy client id" aria-label={`Copy the client id of ${client.name}`} onClick={() => copy(client.id)}><Icon name="file" /></button></span>], ["Secret", `${client.secretHint}… · ${client.secretRotatedAt ? `rotated ${when(client.secretRotatedAt)}` : "original"}`], ["Sees", clientSees(client)], ["Rate limit", `${client.rateLimitPerMinute.toLocaleString()} requests a minute`], ["Expires", when(client.expiresAt)], ["Created", `${when(client.createdAt)} by ${client.createdBy}`], ["Last used", relative(client.lastUsedAt)]]} /></Section>
    <Section title="Scopes"><ul className="checks">{Object.keys(scopeLabelsFor(client.scopes)).map(scope => <li key={scope}><span>{scopeDescription(scope)}</span><StatusBadge tone={client.scopes.includes(scope) ? "ok" : "neutral"}>{client.scopes.includes(scope) ? "Allowed" : "Not allowed"}</StatusBadge></li>)}</ul></Section>
    {secret && <Section title="New secret · Shown once"><SecretValue value={secret} label="Copy the new secret" /></Section>}
    {client.status === "active" && <div className="act developer-actions"><button type="button" className="btn" onClick={() => setMode("edit")}><Icon name="key" />Edit scopes</button><button type="button" className="btn" onClick={() => setMode("rotate")}><Icon name="cycle" />Rotate secret</button><button type="button" className="btn danger" onClick={() => setMode("revoke")}><Icon name="x" />Revoke client</button></div>}
    {mode === "edit" && <ActionDialog title="Edit scopes and limit" onCancel={() => setMode(null)} wide actions={<button type="button" className="btn primary" disabled={busy || scopes.length === 0 || rate < 1 || rate > 60_000} onClick={() => void save()}>Save access</button>}><div className="stack"><ScopeChoices selected={scopes} onChange={setScopes} /><label className="field"><span className="lbl">Rate limit</span><input className="input num" type="number" min={1} max={60000} value={rate} onChange={event => setRate(Number(event.target.value))} /><span className="help">Requests a minute, from 1 to 60,000.</span></label></div></ActionDialog>}
    {mode === "rotate" && <ActionDialog title="Rotate this client secret?" onCancel={() => setMode(null)} actions={<button type="button" className="btn primary" disabled={busy} onClick={() => void rotate()}><Icon name="cycle" />Rotate secret</button>}><p>The current secret will keep working for 24 hours so the application can switch safely. The new secret is shown once.</p></ActionDialog>}
    {mode === "revoke" && <ActionDialog title="Revoke this API client?" onCancel={() => setMode(null)} actions={<button type="button" className="btn danger" disabled={busy} onClick={() => void revoke()}><Icon name="x" />Revoke client</button>}><div className="note bad"><strong>Every token stops at once.</strong><span>The client cannot be restored; create another client if the application needs access again.</span></div></ActionDialog>}
  </>;
}

const scopeLabelsFor = (selected: readonly string[]) => Object.fromEntries([...new Set([...Object.keys({
  "units.read": true, "cover.read": true, "claims.read": true, "claims.write": true, "rma.read": true,
  "rma.write": true, "certificates.read": true, "events.read": true, "units.write-installation": true,
  "units.write-asset-tag": true,
}), ...selected])].map(scope => [scope, true]));

export function ScopeChoices({ selected, onChange }: { selected: string[]; onChange: (value: string[]) => void }) {
  return <div className="developer-checks" aria-label="Public API scopes">{Object.keys(scopeLabelsFor(selected)).map(scope => <label className="checkrow" key={scope}><input type="checkbox" checked={selected.includes(scope)} onChange={event => onChange(event.target.checked ? [...selected, scope].sort() : selected.filter(value => value !== scope))} /><span>{scopeDescription(scope)}</span></label>)}</div>;
}

export function SecretValue({ value, label }: { value: string; label: string }) {
  return <div className="developer-secret"><code>{value}</code><button type="button" className="icon-btn" title={label} aria-label={label} onClick={() => void navigator.clipboard.writeText(value)}><Icon name="file" /></button></div>;
}
