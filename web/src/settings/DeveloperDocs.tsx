import { useEffect, useMemo, useState, type ReactNode } from "react";
import { useSearchParams } from "react-router";
import { StatusBadge } from "../components";
import { useSession } from "../session/session";
import { Icon } from "../shell/Icons";
import { PageHeader } from "../shell/PageHeader";
import { ErrorNote } from "../units/common";
import { developerApi, type CatalogueEntry, type DeveloperCatalogue } from "./developers/developerApi";
import { prettyJson, scopeDescription } from "./developers/developerModel";
import { signatureSamples, type SampleLanguage } from "./developers/signatureSamples";
import "./developers/developer.css";

type Tab = "start" | "auth" | "api" | "webhooks" | "events";
const tabs: Array<[Tab, string]> = [["start", "Getting started"], ["auth", "Authentication"], ["api", "API reference"], ["webhooks", "Webhooks and signatures"], ["events", "Event catalogue"]];
const openApiDownload = "/api/public/v1/openapi.json";

export function Component() {
  const permitted = useSession().roles.includes("tenant_administrator");
  const [search, setSearch] = useSearchParams();
  const requested = search.get("tab");
  const tab: Tab = tabs.some(([value]) => value === requested) ? requested as Tab : "start";
  const [catalogue, setCatalogue] = useState<DeveloperCatalogue | null>(null);
  const [openApi, setOpenApi] = useState<Record<string, unknown> | null>(null);
  const [error, setError] = useState<unknown>(null);
  useEffect(() => {
    if (!permitted) return;
    const controller = new AbortController();
    Promise.all([developerApi.catalogue(controller.signal), developerApi.openApi(controller.signal)]).then(([events, document]) => { setCatalogue(events); setOpenApi(document); }).catch(reason => { if (!controller.signal.aborted) setError(reason); });
    return () => controller.abort();
  }, [permitted]);
  return <main className="content" id="main" tabIndex={-1}>
    <PageHeader eyebrow="Settings · Developers · public API v1" title="Developer documentation" sub="Everything your developers need to build an application on Yojana: authentication, the API, the events and how to check a signature."
      actions={permitted && <a className="btn" href={openApiDownload} download="yojana-public-api-v1.openapi.json"><Icon name="down" />OpenAPI file</a>} />
    {!permitted ? <div className="note bad"><strong>Tenant administrator access is required.</strong></div> : <div className="stack">
      <ErrorNote error={error} />
      <nav className="tabs developer-tabs" aria-label="Documentation sections">{tabs.map(([value, label]) => <button type="button" key={value} aria-current={tab === value ? "page" : undefined} onClick={() => setSearch(value === "start" ? {} : { tab: value })}>{label}</button>)}</nav>
      {tab === "start" && <GettingStarted />}{tab === "auth" && <Authentication scopes={catalogue?.scopes ?? []} />}{tab === "api" && <ApiReference document={openApi} />}{tab === "webhooks" && <WebhookDocumentation sample={catalogue?.events.find(event => event.type === "unit.shipped") ?? null} />}{tab === "events" && <EventCatalogue events={catalogue?.events ?? []} />}
    </div>}
  </main>;
}

function GettingStarted() {
  return <div className="developer-doc-grid"><DocCard title="1 · Create a client"><p>Create an API client, choose the narrowest scopes, and copy its secret when it appears. The secret cannot be recovered later.</p><code>Settings → Developers → API clients</code></DocCard><DocCard title="2 · Exchange credentials"><p>Send the client id and secret to the token endpoint. The returned opaque bearer token lasts one hour.</p><pre className="developer-code">POST /api/public/v1/token{"\n"}Content-Type: application/json{"\n\n"}&#123; "grantType": "client_credentials", "clientId": "yjc_…", "clientSecret": "…" &#125;</pre></DocCard><DocCard title="3 · Call the API"><p>Send the token in the Authorization header. List endpoints use opaque keyset cursors; follow <code>nextCursor</code> without interpreting it.</p><pre className="developer-code">Authorization: Bearer yjt_…{"\n"}Accept: application/json</pre></DocCard><DocCard title="4 · Subscribe to changes"><p>Register a public HTTPS webhook, complete its verification handshake, and check every signature before parsing a message.</p><p className="muted">Deliveries are at least once. Keep processed event ids so duplicates are harmless.</p></DocCard></div>;
}

function Authentication({ scopes }: { scopes: string[] }) {
  return <div className="developer-doc-grid"><DocCard title="Client credentials"><p>Client ids and secrets are exchanged for a short-lived opaque token at <code>POST /api/public/v1/token</code>. Cookies are not accepted on the public API, and bearer tokens are not accepted on internal screens.</p><p>Store the secret in the application's secret store. Rotation leaves the previous secret valid for 24 hours; revocation stops every token immediately.</p><div className="note"><strong>Rate limits</strong><span>A response with status 429 includes <code>Retry-After</code>. Wait for that period before trying again.</span></div></DocCard><DocCard title="Scopes"><ul className="developer-scope-list">{scopes.map(scope => <li key={scope}><code>{scope}</code><span>{scopeDescription(scope)}</span></li>)}</ul></DocCard></div>;
}

function ApiReference({ document }: { document: Record<string, unknown> | null }) {
  const operations = useMemo(() => apiOperations(document), [document]);
  if (!document) return <section className="card"><div className="card-body muted">Loading API description…</div></section>;
  return <div className="stack"><div className="note"><strong>Generated from the running public API</strong><span>Only <code>/api/public/v1</code> operations appear here. The downloadable file is the same document.</span></div>{operations.map(operation => <section className="card developer-operation" key={`${operation.method}:${operation.path}`}><div className="card-header"><h2><StatusBadge tone={operation.method === "GET" ? "info" : "warn"}>{operation.method}</StatusBadge> <code>{operation.path}</code></h2></div><div className="card-body stack">{operation.summary && <p>{operation.summary}</p>}<p><strong>Scopes:</strong> <code>{operation.scopes.length ? operation.scopes.join(", ") : "No additional scope declared"}</code></p>{operation.parameters.length > 0 && <div><h3>Parameters</h3><table className="table dense"><thead><tr><th>Name</th><th>In</th><th>Required</th><th>Schema</th></tr></thead><tbody>{operation.parameters.map((parameter, index) => <tr key={`${parameter.name}:${index}`}><td><code>{parameter.name}</code></td><td>{parameter.place}</td><td>{parameter.required ? "Yes" : "No"}</td><td><code>{parameter.schema}</code></td></tr>)}</tbody></table></div>}<SchemaPart title="Request schema" value={operation.request} /><SchemaPart title="Response schemas" value={operation.responses} /></div></section>)}</div>;
}

function WebhookDocumentation({ sample }: { sample: CatalogueEntry | null }) {
  const [language, setLanguage] = useState<SampleLanguage>("csharp");
  return <div className="developer-doc-split"><section className="card"><div className="card-header"><h2>Check that a message came from Yojana</h2></div><div className="card-body stack"><p>Every message carries a <code>Yojana-Signature</code> header: a timestamp and an HMAC-SHA256 of <code>&lt;timestamp&gt;.&lt;raw body&gt;</code> with the endpoint's signing secret.</p><pre className="developer-code">Yojana-Signature: t=1794044832,v1=d3efb7f8…60afc{"\n"}Yojana-Event-Id: 0193f6a1-7c2e-7b44-9d10-5e2a8c1f0b37{"\n"}Yojana-Event-Type: unit.shipped{"\n"}Yojana-Delivery-Attempt: 1</pre><ol className="developer-steps"><li>Refuse the message if the timestamp differs from your clock by more than 5 minutes.</li><li>Compute the HMAC over the raw body as received, before parsing it.</li><li>Accept it if <strong>any</strong> <code>v1</code> value matches. During rotation there are two.</li><li>Ignore an event id already processed. A message can arrive more than once.</li></ol><div className="segmented" role="group" aria-label="Sample language">{(Object.keys(signatureSamples) as SampleLanguage[]).map(value => <button type="button" key={value} aria-pressed={language === value} onClick={() => setLanguage(value)}>{signatureSamples[value].label}</button>)}</div><pre className="developer-code developer-code-scroll">{signatureSamples[language].code}</pre></div></section><section className="card"><div className="card-header"><h2>Sample: unit.shipped</h2><span className="meta">version 1</span></div><div className="card-body stack">{sample ? <pre className="developer-code">{prettyJson(sample.sample)}</pre> : <p className="muted">Loading sample…</p>}<p className="help">Messages carry ids and what changed, never names or prices. Read allowed details from <code>GET /api/public/v1/units/&#123;id&#125;</code>.</p></div></section></div>;
}

function EventCatalogue({ events }: { events: CatalogueEntry[] }) {
  const [selected, setSelected] = useState("");
  useEffect(() => { if (!selected && events[0]) setSelected(events[0].type); }, [events, selected]);
  const event = events.find(entry => entry.type === selected) ?? events[0];
  return <div className="developer-catalogue"><section className="card"><div className="card-header"><h2>Events</h2><span className="meta">{events.length} public types</span></div><div className="developer-event-list">{events.map(entry => <button type="button" key={entry.type} aria-pressed={entry.type === event?.type} onClick={() => setSelected(entry.type)}><code>{entry.type}</code><StatusBadge tone="neutral">v{entry.version}</StatusBadge></button>)}</div></section>{event && <section className="card"><div className="card-header"><h2><code>{event.type}</code></h2><span className="meta">version {event.version}</span></div><div className="card-body stack"><h3>Fields</h3><table className="table dense"><thead><tr><th>Field</th><th>Type</th><th>Required</th></tr></thead><tbody>{schemaFields(event.schema).map(field => <tr key={field.name}><td><code>{field.name}</code></td><td>{field.type}</td><td>{field.required ? "Yes" : "No"}</td></tr>)}</tbody></table><h3>Sample message</h3><pre className="developer-code">{prettyJson(event.sample)}</pre><details><summary>JSON Schema</summary><pre className="developer-code">{prettyJson(event.schema)}</pre></details></div></section>}</div>;
}

function DocCard({ title, children }: { title: string; children: ReactNode }) { return <section className="card"><div className="card-header"><h2>{title}</h2></div><div className="card-body stack">{children}</div></section>; }
function SchemaPart({ title, value }: { title: string; value: unknown }) { return value === null ? null : <details><summary>{title}</summary><pre className="developer-code">{prettyJson(asRecord(value))}</pre></details>; }
const asRecord = (value: unknown): Record<string, unknown> => typeof value === "object" && value !== null && !Array.isArray(value) ? value as Record<string, unknown> : { value };

interface Operation { path: string; method: string; summary: string | null; scopes: string[]; parameters: Array<{ name: string; place: string; required: boolean; schema: string }>; request: unknown; responses: unknown }
function apiOperations(document: Record<string, unknown> | null): Operation[] {
  const paths = document ? asRecord(document.paths) : {}; const operations: Operation[] = [];
  for (const [path, value] of Object.entries(paths)) for (const [method, operationValue] of Object.entries(asRecord(value))) {
    if (!(["get", "post", "put", "patch", "delete"] as string[]).includes(method)) continue;
    const operation = asRecord(operationValue); const security = Array.isArray(operation.security) ? operation.security : [];
    // Contract with PublicOpenApi: bearer security has no OAuth scope slot, so the explicit extension supplies it.
    const extensionScopes = Array.isArray(operation["x-yojana-scopes"])
      ? operation["x-yojana-scopes"].filter((scope): scope is string => typeof scope === "string") : [];
    const scopes = [...new Set([...security.flatMap(item => Object.values(asRecord(item)).flatMap(value => Array.isArray(value) ? value.filter((scope): scope is string => typeof scope === "string") : [])), ...extensionScopes])];
    const rawParameters = Array.isArray(operation.parameters) ? operation.parameters : [];
    operations.push({ path, method: method.toUpperCase(), summary: typeof operation.summary === "string" ? operation.summary : null, scopes,
      parameters: rawParameters.map(value => asRecord(value)).map(parameter => ({ name: typeof parameter.name === "string" ? parameter.name : "parameter", place: typeof parameter.in === "string" ? parameter.in : "—", required: parameter.required === true, schema: schemaLabel(parameter.schema) })), request: operation.requestBody ?? null, responses: operation.responses ?? null });
  }
  return operations.sort((left, right) => left.path.localeCompare(right.path) || left.method.localeCompare(right.method));
}
function schemaLabel(value: unknown): string { const schema = asRecord(value); if (typeof schema.$ref === "string") return schema.$ref.split("/").at(-1) ?? schema.$ref; if (typeof schema.type === "string") return schema.type; return "object"; }
function schemaFields(schema: Record<string, unknown>): Array<{ name: string; type: string; required: boolean }> { const properties = asRecord(schema.properties); const required = Array.isArray(schema.required) ? schema.required.filter((value): value is string => typeof value === "string") : []; return Object.entries(properties).map(([name, value]) => ({ name, type: schemaLabel(value), required: required.includes(name) })); }
