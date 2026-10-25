import { useCallback, useEffect, useMemo, useState } from "react";
import { Link } from "react-router";
import { ApiError } from "../api/client";
import { LegalEntityPicker, useLegalEntities } from "../api/legalEntities";
import { ActionDialog, DataGrid, FormActions, KpiStrip, StatusBadge, type GridColumn, type PageResult } from "../components";
import { CustomerField, ItemFamilyField, useReferenceNames } from "../corrections/ReferenceFields";
import { useSession } from "../session/session";
import { Icon } from "../shell/Icons";
import { openDrawer, updateDrawer } from "../shell/drawerStore";
import { PageHeader } from "../shell/PageHeader";
import { ErrorNote, Kv, Section } from "../units/common";
import { SecretValue } from "./DeveloperClients";
import { developerApi, type CatalogueEntry, type DeveloperCatalogue, type WebhookPage, type WebhookRequest, type WebhookSettings, type WebhookSubscription } from "./developers/developerApi";
import { webhookStatus, when } from "./developers/developerModel";
import "./developers/developer.css";

const PAGE_SIZE = 50;

export function Component() {
  const permitted = useSession().roles.includes("tenant_administrator");
  const [result, setResult] = useState<WebhookPage | null>(null);
  const [catalogue, setCatalogue] = useState<DeveloperCatalogue | null>(null);
  const [settings, setSettings] = useState<WebhookSettings | null>(null);
  const [page, setPage] = useState(1);
  const [cursors, setCursors] = useState<(string | undefined)[]>([undefined]);
  const [newOpen, setNewOpen] = useState(false);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<unknown>(null);

  const load = useCallback(async (target = page, cursor = cursors[target - 1]) => {
    setLoading(true); setError(null);
    try {
      const [next, eventCatalogue, currentSettings] = await Promise.all([
        developerApi.webhooks(cursor), catalogue ? Promise.resolve(catalogue) : developerApi.catalogue(), settings ? Promise.resolve(settings) : developerApi.settings(),
      ]);
      setResult(next); setCatalogue(eventCatalogue); setSettings(currentSettings); setPage(target);
      if (next.nextCursor) setCursors(current => { const copy = current.slice(0, target); copy[target] = next.nextCursor!; return copy; });
    } catch (reason) { setError(reason); }
    finally { setLoading(false); }
  }, [catalogue, cursors, page, settings]);
  useEffect(() => { if (permitted) void load(1, undefined); }, [permitted]); // eslint-disable-line react-hooks/exhaustive-deps

  const show = useCallback((row: WebhookSubscription) => {
    const head = { eyebrow: "Webhook endpoint", title: row.name };
    const changed = (next: WebhookSubscription, secret?: string) => { updateDrawer(head, <WebhookDrawer webhook={next} initialSecret={secret} onChanged={changed} />); void load(); };
    openDrawer(head, <WebhookDrawer webhook={row} onChanged={changed} />);
  }, [catalogue?.events, load]);
  const columns = useMemo<GridColumn<WebhookSubscription>[]>(() => [
    { key: "endpoint", label: "Endpoint", width: 40, render: row => <span><span className="primary">{row.name}</span><span className="sub mono">{row.url}</span></span> },
    { key: "events", label: "Events", width: 30, render: row => `${row.eventTypes.length === (catalogue?.events.length ?? -1) - 1 ? "All" : row.eventTypes.slice(0, 3).join(", ")}${row.eventTypes.length > 3 ? ` · +${row.eventTypes.length - 3}` : ""}` },
    { key: "last", label: "Last 24 h", width: 14, numeric: true, render: row => row.status === "pending_verification" ? "—" : `${row.activity.deliveredLast24Hours.toLocaleString()} of ${row.activity.totalLast24Hours.toLocaleString()}` },
    { key: "status", label: "Status", width: 16, render: row => { const status = webhookStatus(row.status, row.failingSince); return <StatusBadge tone={status.tone}>{status.label}</StatusBadge>; } },
    { key: "actions", label: "", width: "90px", render: row => <span className="row-actions">{row.status === "pending_verification" ? <button type="button" className="icon-btn" title="Verify again" aria-label={`Send verification again to ${row.name}`} onClick={() => void developerApi.webhookAction(row.id, "verify").then(next => { void load(); show(next); }).catch(setError)}><Icon name="cycle" /></button> : row.status === "paused" ? <button type="button" className="icon-btn" title="Resume" aria-label={`Resume ${row.name}`} onClick={() => void developerApi.webhookAction(row.id, "resume").then(next => { void load(); show(next); }).catch(setError)}><Icon name="chev" /></button> : <button type="button" className="icon-btn" title="Pause" aria-label={`Pause ${row.name}`} onClick={() => void developerApi.webhookAction(row.id, "pause").then(next => { void load(); show(next); }).catch(setError)}><Icon name="range" /></button>}<Link className="icon-btn" title="Delivery log" aria-label={`Open the delivery log of ${row.name}`} to={`/settings/developers/webhooks/${row.id}/deliveries`}><Icon name="list" /></Link></span> },
  ], [catalogue?.events.length, load, show]);
  const rows = result?.items ?? [];
  const grid: PageResult<WebhookSubscription> = { rows, page, pageSize: PAGE_SIZE,
    total: (page - 1) * PAGE_SIZE + rows.length + (result?.nextCursor ? 1 : 0), continuation: result?.nextCursor ?? null,
    totalIsExact: false, hasMore: !!result?.nextCursor };
  return <main className="content" id="main" tabIndex={-1}>
    <PageHeader eyebrow="Settings · Developers · tenant administrators only" title="Webhooks"
      sub="Yojana tells your applications when something changes. Each message is signed so the application can check it came from Yojana."
      actions={permitted && <><Link className="btn" to="/settings/developers/docs?tab=events"><Icon name="book" />Event catalogue</Link><button type="button" className="btn primary" onClick={() => setNewOpen(value => !value)}><Icon name="plus" />New endpoint</button></>} />
    {!permitted ? <div className="note bad"><strong>Tenant administrator access is required.</strong></div> : <div className="stack">
      <ErrorNote error={error} onReload={() => void load()} />
      <KpiStrip label="Webhook figures" kpis={[
        { label: "Delivered, last 24 h", value: result?.summary.deliveredLast24Hours.toLocaleString() ?? "—", detail: "successful deliveries" },
        { label: "Waiting to retry", value: result?.summary.waitingToRetry.toLocaleString() ?? "—", detail: "retried for up to 24 h" },
        { label: "Parked", value: result?.summary.parked.toLocaleString() ?? "—", detail: "redeliver after the endpoint recovers" },
        { label: "Paused endpoints", value: result?.summary.pausedEndpoints.toLocaleString() ?? "—", detail: "administrators are e-mailed" },
      ]} />
      {newOpen && catalogue && <NewWebhookForm events={catalogue.events.filter(event => event.type !== "endpoint.verify")} onCreated={(created) => { void load(1, undefined); show(created); }} onCancel={() => setNewOpen(false)} />}
      <RetentionSetting value={settings} onChanged={setSettings} />
      <section className="card" aria-labelledby="webhook-list"><div className="card-header"><h2 id="webhook-list">Endpoints</h2><span className="meta">needing attention first</span></div>
        <DataGrid columns={columns} page={grid} rowId={row => row.id} onPage={target => { if (target < page || result?.nextCursor) void load(target, cursors[target - 1]); }} onRowClick={show} loading={loading} emptyText="No webhook endpoints have been registered." viewportHeight={560} />
      </section>
    </div>}
  </main>;
}

function RetentionSetting({ value, onChanged }: { value: WebhookSettings | null; onChanged: (value: WebhookSettings) => void }) {
  const [days, setDays] = useState(value?.retentionDays ?? 30);
  const [busy, setBusy] = useState(false); const [error, setError] = useState<unknown>(null);
  useEffect(() => { if (value) setDays(value.retentionDays); }, [value]);
  const changed = value !== null && days !== value.retentionDays;
  return <section className="card developer-retention" aria-labelledby="webhook-retention"><div className="card-header"><h2 id="webhook-retention">Delivery log retention</h2><span className="meta">tenant setting · default 30 days</span></div><div className="card-body"><ErrorNote error={error} /><div className="developer-inline-form"><label className="field"><span className="lbl">Keep delivery history</span><span className="developer-input-suffix"><input className="input num" type="number" min={1} max={3650} value={days} onChange={event => setDays(Number(event.target.value))} /><span>days</span></span><span className="help">Attempts, replies and message bodies are removed after this period.</span></label><FormActions><button type="button" className="btn primary" disabled={!changed || busy || days < 1 || days > 3650} onClick={() => { setBusy(true); setError(null); void developerApi.updateSettings(days).then(onChanged).catch(setError).finally(() => setBusy(false)); }}>Save retention</button></FormActions></div></div></section>;
}

function NewWebhookForm({ events, onCreated, onCancel }: { events: CatalogueEntry[]; onCreated: (created: WebhookSubscription) => void; onCancel: () => void }) {
  const legalEntities = useLegalEntities();
  const [name, setName] = useState(""); const [url, setUrl] = useState(""); const [selected, setSelected] = useState<string[]>([]);
  const [itemFamily, setItemFamily] = useState<string | null>(null); const [customer, setCustomer] = useState<string | null>(null); const [legalEntity, setLegalEntity] = useState("");
  const [secret, setSecret] = useState<string | null>(null); const [busy, setBusy] = useState(false); const [error, setError] = useState<unknown>(null); const [urlError, setUrlError] = useState<string | null>(null);
  const ready = Boolean(name.trim() && url.trim() && selected.length > 0);
  const create = async () => {
    setBusy(true); setError(null); setUrlError(null);
    const request: WebhookRequest = { name: name.trim(), url: url.trim(), eventTypes: selected,
      filters: { itemFamilies: itemFamily ? [itemFamily] : [], customerPartyRefs: customer ? [customer] : [], legalEntityIds: legalEntity ? [legalEntity] : [] }, relayId: null };
    try { const created = await developerApi.createWebhook(request); setSecret(created.secret); onCreated(created.subscription); }
    catch (reason) { if (reason instanceof ApiError && reason.rule === "webhook_private_target_needs_relay") setUrlError("This address is on a private network; Yojana can only deliver to public HTTPS addresses."); else setError(reason); }
    finally { setBusy(false); }
  };
  return <section className="card" aria-labelledby="new-webhook"><div className="card-header"><h2 id="new-webhook">New endpoint</h2><span className="meta">verified before activation</span></div><div className="card-body stack"><ErrorNote error={error} /><div className="form-grid">
    <label className="field"><span className="lbl">Name</span><input className="input" maxLength={120} value={name} onChange={event => setName(event.target.value)} /></label>
    <label className="field"><span className="lbl">Public HTTPS address</span><input className="input mono" type="url" aria-invalid={urlError ? true : undefined} aria-describedby={urlError ? "webhook-url-error" : undefined} value={url} onChange={event => { setUrl(event.target.value); setUrlError(null); }} placeholder="https://hooks.example.com/yojana" />{urlError && <span className="help bad" id="webhook-url-error" role="alert">{urlError}</span>}<span className="help">Private and internal addresses are refused. Relay delivery is not available yet.</span></label>
    <div className="field span"><span className="lbl">Events</span><div className="developer-event-picker">{events.map(event => <label className="checkrow" key={event.type}><input type="checkbox" checked={selected.includes(event.type)} onChange={change => setSelected(change.target.checked ? [...selected, event.type].sort() : selected.filter(type => type !== event.type))} /><span><strong className="mono">{event.type}</strong><small>version {event.version}</small></span></label>)}</div></div>
    <ItemFamilyField id="webhook-item-family" label="Item family" optional value={itemFamily} onChange={setItemFamily} hint="Leave empty for every item family." />
    <CustomerField id="webhook-customer" label="Customer" optional value={customer} onChange={setCustomer} hint="Leave empty for every customer." />
    <LegalEntityPicker id="webhook-legal-entity" label="Legal entity" value={legalEntity} entities={legalEntities.value?.entities ?? []} onChange={setLegalEntity} hint="Leave empty for every legal entity." />
  </div>{secret && <div className="note warn"><strong>Signing secret · shown once</strong><SecretValue value={secret} label="Copy the webhook signing secret" /></div>}<FormActions status={secret ? "Endpoint created and verified." : undefined}><button type="button" className="btn" onClick={onCancel}>{secret ? "Done" : "Cancel"}</button><button type="button" className="btn primary" disabled={!ready || busy || secret !== null} onClick={() => void create()}><Icon name="plus" />Create endpoint</button></FormActions></div></section>;
}

function WebhookDrawer({ webhook, initialSecret = null, onChanged }: { webhook: WebhookSubscription; initialSecret?: string | null | undefined; onChanged: (value: WebhookSubscription, secret?: string) => void }) {
  const names = useReferenceNames({ parties: [...webhook.filters.customerPartyRefs, ...webhook.filters.legalEntityIds] });
  const [confirm, setConfirm] = useState<"rotate" | "delete" | null>(null); const [secret, setSecret] = useState<string | null>(initialSecret); const [error, setError] = useState<unknown>(null); const [busy, setBusy] = useState(false);
  const filters = [...webhook.filters.itemFamilies.map(value => `Item family ${value}`), ...webhook.filters.customerPartyRefs.map(value => `Customer ${names.party(value)}`), ...webhook.filters.legalEntityIds.map(value => `Legal entity ${names.party(value)}`)];
  const action = async (name: "pause" | "resume" | "verify") => { setBusy(true); setError(null); try { onChanged(await developerApi.webhookAction(webhook.id, name)); } catch (reason) { setError(reason); } finally { setBusy(false); } };
  const rotate = async () => { setBusy(true); setError(null); try { const result = await developerApi.rotateWebhook(webhook.id); setSecret(result.secret); setConfirm(null); onChanged(result.subscription, result.secret); } catch (reason) { setError(reason); } finally { setBusy(false); } };
  return <><ErrorNote error={error} />{webhook.status === "paused" && <div className="note bad"><strong>Paused.</strong><span>{webhook.failingSince ? `No successful delivery since ${when(webhook.failingSince)}.` : "Delivery is paused."} {webhook.activity.parked.toLocaleString()} messages are parked.</span></div>}
    <Section title="Settings"><Kv rows={[["Address", <span className="mono developer-break" key="url">{webhook.url}</span>], ["Events", webhook.eventTypes.join(" · ")], ["Filters", filters.join(" · ") || "Every record"], ["Signing secret", webhook.previousSecretExpiresAt ? `Rotated · previous works until ${when(webhook.previousSecretExpiresAt)}` : "Current secret only"], ["Rate", `${webhook.ratePerSecond} messages a second`], ["Verified", when(webhook.verifiedAt)]]} /></Section>
    {secret && <Section title="New signing secret · shown once"><SecretValue value={secret} label="Copy the new signing secret" /></Section>}
    <div className="act developer-actions">{webhook.status === "paused" ? <button type="button" className="btn primary" disabled={busy} onClick={() => void action("resume")}><Icon name="chev" />Resume</button> : webhook.status === "pending_verification" ? <button type="button" className="btn primary" disabled={busy} onClick={() => void action("verify")}><Icon name="cycle" />Verify again</button> : <button type="button" className="btn" disabled={busy} onClick={() => void action("pause")}><Icon name="range" />Pause</button>}<button type="button" className="btn" disabled={busy || webhook.status !== "active"} onClick={() => void developerApi.testWebhook(webhook.id, webhook.eventTypes[0]).catch(setError)}><Icon name="mail" />Send test event</button><button type="button" className="btn" disabled={busy || webhook.status === "disabled"} onClick={() => setConfirm("rotate")}><Icon name="key" />Rotate secret</button><Link className="btn" to={`/settings/developers/webhooks/${webhook.id}/deliveries`}><Icon name="list" />Delivery log</Link><button type="button" className="btn danger" disabled={busy || webhook.status === "disabled"} onClick={() => setConfirm("delete")}><Icon name="x" />Delete endpoint</button></div>
    {confirm === "rotate" && <ActionDialog title="Rotate this signing secret?" onCancel={() => setConfirm(null)} actions={<button type="button" className="btn primary" disabled={busy} onClick={() => void rotate()}><Icon name="key" />Rotate secret</button>}><p>For 24 hours messages carry signatures from both secrets, so the receiving application can switch safely.</p></ActionDialog>}
    {confirm === "delete" && <ActionDialog title="Delete this endpoint?" onCancel={() => setConfirm(null)} actions={<button type="button" className="btn danger" disabled={busy} onClick={() => { setBusy(true); void developerApi.deleteWebhook(webhook.id).then(() => onChanged({ ...webhook, status: "disabled" })).catch(setError).finally(() => { setBusy(false); setConfirm(null); }); }}><Icon name="x" />Delete endpoint</button>}><div className="note bad"><strong>Delivery stops permanently.</strong><span>The delivery log remains until its retention period expires.</span></div></ActionDialog>}
  </>;
}
