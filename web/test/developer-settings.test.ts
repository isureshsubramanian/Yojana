import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import { developerApi } from "../src/settings/developers/developerApi.ts";
import { answer, clientSees, clientStatus, deliveryStatus, prettyJson, statusFilter } from "../src/settings/developers/developerModel.ts";

const source = (path: string) => readFileSync(new URL(`../src/${path}`, import.meta.url), "utf8");
const clients = source("settings/DeveloperClients.tsx");
const newClient = source("settings/DeveloperClientNew.tsx");
const webhooks = source("settings/DeveloperWebhooks.tsx");
const deliveries = source("settings/DeveloperDeliveries.tsx");
const docs = source("settings/DeveloperDocs.tsx");
const api = source("settings/developers/developerApi.ts");
const registry = source("settings/registry.ts");
const routes = source("routes.tsx");

test("API clients screen keeps the vetted KPIs, keyset grid, drawer actions and one-time secret flow", () => {
  for (const label of ["Active clients", "Last used", "Revoked clients", "Secrets older than 1 year", "Edit scopes", "Rotate secret", "Revoke client", "Shown once"])
    assert.ok(clients.includes(label), label);
  for (const token of ["<DataGrid", "next", "openDrawer", "<ActionDialog", "<SecretValue", "<KpiStrip"])
    assert.ok(clients.includes(token), token);
  assert.match(clients, /title="Copy client id" aria-label=/);
  assert.doesNotMatch(clients, /Requests, last 24 h/);
});

test("new API client uses the house form actions and clears its secret with page state", () => {
  for (const label of ["New API client", "Disclosure tier", "Limit to one customer", "Rate limit", "Expires", "Sandbox client", "Create client", "shown once"])
    assert.match(newClient.toLocaleLowerCase(), new RegExp(label.toLocaleLowerCase()));
  assert.match(newClient, /<CustomerField/);
  assert.match(newClient, /<ScopeChoices/);
  assert.match(newClient, /<FormActions/);
  assert.match(newClient, /useState<ApiClientCreated \| null>/);
  assert.doesNotMatch(newClient, /window\.confirm|window\.alert/);
});

test("webhooks screen has attention-first paging, catalogue events, public-address feedback, filters and retention", () => {
  for (const label of ["Delivered, last 24 h", "Waiting to retry", "Parked", "Paused endpoints", "needing attention first", "New endpoint", "Delivery log retention", "Save retention", "Send test event", "Delete endpoint"])
    assert.ok(webhooks.includes(label), label);
  for (const component of ["<ItemFamilyField", "<CustomerField", "<LegalEntityPicker", "<DataGrid", "<FormActions", "<ActionDialog"])
    assert.ok(webhooks.includes(component), component);
  assert.match(webhooks, /webhook_private_target_needs_relay/);
  assert.match(webhooks, /Yojana can only deliver to public HTTPS addresses/);
  assert.doesNotMatch(webhooks, /relayId: (?!null)/);
  assert.doesNotMatch(webhooks, /window\.confirm|window\.alert/);
});

test("delivery log uses server totals and counts with all filters and replay actions", () => {
  for (const label of ["All", "Delivered", "Retrying", "Parked", "Discarded", "Event type: any", "Redeliver a period", "Send test event", "Attempts", "Message", "Redeliver now", "Discard"])
    assert.ok(deliveries.includes(label), label);
  assert.match(deliveries, /total: result\?\.total/);
  assert.match(deliveries, /nextCursor/);
  assert.match(deliveries, /type="date"/);
  assert.match(deliveries, /<ActionDialog title="Discard/);
});

test("developer documentation renders every approved tab from live descriptions and tested samples", () => {
  for (const label of ["Getting started", "Authentication", "API reference", "Webhooks and signatures", "Event catalogue", "OpenAPI file", "Request schema", "Response schemas", "Parameters", "Scopes", "Sample message", "JSON Schema"])
    assert.ok(docs.includes(label), label);
  assert.match(docs, /developerApi\.openApi/);
  assert.match(docs, /developerApi\.catalogue/);
  assert.match(docs, /signatureSamples/);
  assert.match(docs, /x-yojana-scopes/);
  assert.doesNotMatch(docs, /iframe|dangerouslySetInnerHTML/);
});

test("developer settings routes are grouped and administrator-only", () => {
  for (const name of ["API clients", "Webhooks", "Developer documentation"])
    assert.match(registry, new RegExp(`group: "Developers", name: "${name}"[\\s\\S]{0,260}roles: administrator`));
  assert.match(routes, /path: "\/settings\/developers\/clients\/new"/);
  assert.match(routes, /path: "\/settings\/developers\/webhooks\/:id\/deliveries"/);
  for (const screen of [clients, newClient, webhooks, deliveries, docs]) assert.match(screen, /roles\.includes\("tenant_administrator"\)/);
});

test("developer API decodes valid client pages and refuses invalid shapes at the boundary", async () => {
  const original = globalThis.fetch;
  globalThis.fetch = (async () => new Response(JSON.stringify({ items: [{ id: "yjc_tenant_client", name: "Fictional service console", scopes: ["units.read"], disclosureTier: "back_office", customerParty: null, sandbox: false, status: "active", rateLimitPerMinute: 600, expiresAt: null, createdAt: "2026-09-01T09:00:00Z", createdBy: "Fictional Administrator", secretHint: "abcd1234", secretRotatedAt: null, revokedAt: null, revokedBy: null, lastUsedAt: "2026-09-01T10:00:00Z" }], next: "opaque", summary: { active: 1, sandbox: 0, revoked: 0, secretsOlderThanOneYear: 0, lastUsedAt: "2026-09-01T10:00:00Z" } }), { status: 200 })) as typeof fetch;
  try {
    const page = await developerApi.clients("cursor value");
    assert.equal(page.items[0]?.name, "Fictional service console");
    assert.equal(page.summary.active, 1);
  } finally { globalThis.fetch = original; }
  globalThis.fetch = (async () => new Response(JSON.stringify({ items: "not-an-array", next: null, summary: {} }), { status: 200 })) as typeof fetch;
  try { await assert.rejects(() => developerApi.clients(), /Invalid API client page\.items response/); }
  finally { globalThis.fetch = original; }
  assert.match(api, /invalid server response is refused before it reaches a component/);
});

test("display models label access and delivery state without exposing raw people ids", () => {
  const client = { disclosureTier: "customer", customerParty: { code: "C-10022", name: "Helix Systems Ltd" }, sandbox: false, status: "active", expiresAt: null, revokedAt: null } as Parameters<typeof clientSees>[0];
  assert.equal(clientSees(client), "Customer tier · Helix Systems Ltd only");
  assert.equal(clientStatus(client).label, "Active");
  assert.equal(statusFilter("retrying"), "pending");
  const delivery = { status: "pending", nextAttemptAt: "2026-09-01T10:03:00Z", lastStatusCode: 504, lastOutcome: "failed", lastErrorCode: null } as Parameters<typeof deliveryStatus>[0];
  assert.match(deliveryStatus(delivery).label, /^Retrying/);
  assert.equal(answer(delivery), "504 · failed");
  assert.equal(prettyJson("{\"item\":\"server\"}"), "{\n  \"item\": \"server\"\n}");
});
