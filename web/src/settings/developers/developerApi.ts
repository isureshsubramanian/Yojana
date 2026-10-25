import { api } from "../../api/client.ts";

export type DisclosureTier = "customer" | "back_office";
export type ClientStatus = "active" | "revoked";
export type WebhookStatus = "pending_verification" | "active" | "paused" | "disabled";
export type DeliveryStatus = "pending" | "succeeded" | "parked" | "discarded";

export interface ApiClientParty { code: string; name: string | null }
export interface ApiClientView {
  id: string; name: string; scopes: string[]; disclosureTier: DisclosureTier; customerParty: ApiClientParty | null;
  sandbox: boolean; status: ClientStatus; rateLimitPerMinute: number; expiresAt: string | null; createdAt: string;
  createdBy: string; secretHint: string; secretRotatedAt: string | null; revokedAt: string | null;
  revokedBy: string | null; lastUsedAt: string | null;
}
export interface ApiClientSummary { active: number; sandbox: number; revoked: number; secretsOlderThanOneYear: number; lastUsedAt: string | null }
export interface ApiClientPage { items: ApiClientView[]; next: string | null; summary: ApiClientSummary }
export interface ApiClientCreated { client: ApiClientView; secret: string }
export interface ApiClientSecretRotated { clientId: string; secret: string; previousSecretExpiresAt: string }
export interface CreateApiClientRequest {
  name: string; scopes: string[]; disclosureTier: DisclosureTier; customerPartyId: string | null;
  sandbox: boolean; expiresAt: string | null; rateLimitPerMinute: number;
}

export interface WebhookFilters { itemFamilies: string[]; customerPartyRefs: string[]; legalEntityIds: string[] }
export interface WebhookActivity { deliveredLast24Hours: number; totalLast24Hours: number; parked: number }
export interface WebhookSubscription {
  id: string; name: string; url: string; eventTypes: string[]; filters: WebhookFilters; status: WebhookStatus;
  relayId: string | null; ratePerSecond: number; verifiedAt: string | null; failingSince: string | null;
  pausedAt: string | null; pausedReason: string | null; createdAt: string; updatedAt: string; version: string;
  previousSecretExpiresAt: string | null; activity: WebhookActivity;
}
export interface WebhookSummary { deliveredLast24Hours: number; waitingToRetry: number; parked: number; pausedEndpoints: number }
export interface WebhookPage { items: WebhookSubscription[]; nextCursor: string | null; summary: WebhookSummary }
export interface WebhookSecretResponse { subscription: WebhookSubscription; secret: string }
export interface WebhookActionResponse { subscription: WebhookSubscription }
export interface WebhookRequest { name: string; url: string; eventTypes: string[]; filters: WebhookFilters; relayId: null }

export interface DeliveryStatusCounts { all: number; succeeded: number; pending: number; parked: number; discarded: number }
export interface WebhookDeliveryRow {
  id: string; eventId: string; eventType: string; status: DeliveryStatus; attemptCount: number; createdAt: string;
  nextAttemptAt: string; lastAttemptAt: string | null; succeededAt: string | null; parkedAt: string | null;
  discardedAt: string | null; replayOf: string | null; recordReference: string | null; lastOutcome: string | null;
  lastStatusCode: number | null; lastDurationMs: number | null; lastResponseExcerpt: string | null; lastErrorCode: string | null;
}
export interface DeliveryPage { items: WebhookDeliveryRow[]; nextCursor: string | null; total: number; counts: DeliveryStatusCounts }
export interface DeliveryAttempt { id: string; attemptedAt: string; outcome: string; statusCode: number | null; durationMs: number; responseExcerpt: string | null; errorCode: string | null }
export interface DeliveryDetail { delivery: WebhookDeliveryRow; eventBody: string; attempts: DeliveryAttempt[] }

export interface CatalogueEntry { type: string; version: string; schema: Record<string, unknown>; sample: Record<string, unknown> }
export interface DeveloperCatalogue { events: CatalogueEntry[]; scopes: string[] }
export interface WebhookSettings { retentionDays: number }

export interface DeliveryFilters { status?: DeliveryStatus; eventType?: string; from?: string; to?: string }

const q = (values: Record<string, string | number | undefined>) => {
  const result = new URLSearchParams();
  for (const [key, value] of Object.entries(values)) if (value !== undefined && value !== "") result.set(key, String(value));
  return result.toString();
};

// The project has no runtime-schema dependency and P14 permits no new package. These narrow decoders perform the
// same API-boundary job: an invalid server response is refused before it reaches a component.
const object = (value: unknown, at: string): Record<string, unknown> => {
  if (typeof value !== "object" || value === null || Array.isArray(value)) throw new Error(`Invalid ${at} response.`);
  return value as Record<string, unknown>;
};
const text = (value: unknown, at: string) => { if (typeof value !== "string") throw new Error(`Invalid ${at} response.`); return value; };
const nullableText = (value: unknown, at: string) => value === null ? null : text(value, at);
const number = (value: unknown, at: string) => { if (typeof value !== "number" || !Number.isFinite(value)) throw new Error(`Invalid ${at} response.`); return value; };
const bool = (value: unknown, at: string) => { if (typeof value !== "boolean") throw new Error(`Invalid ${at} response.`); return value; };
const list = <T>(value: unknown, at: string, item: (candidate: unknown, at: string) => T) => {
  if (!Array.isArray(value)) throw new Error(`Invalid ${at} response.`);
  return value.map((candidate, index) => item(candidate, `${at}[${index}]`));
};
const stringList = (value: unknown, at: string) => list(value, at, text);
const oneOf = <T extends string>(value: unknown, choices: readonly T[], at: string): T => {
  const candidate = text(value, at);
  if (!choices.includes(candidate as T)) throw new Error(`Invalid ${at} response.`);
  return candidate as T;
};

const client = (value: unknown, at = "API client"): ApiClientView => {
  const row = object(value, at); const party = row.customerParty === null ? null : object(row.customerParty, `${at}.customerParty`);
  return { id: text(row.id, `${at}.id`), name: text(row.name, `${at}.name`), scopes: stringList(row.scopes, `${at}.scopes`),
    disclosureTier: oneOf(row.disclosureTier, ["customer", "back_office"], `${at}.disclosureTier`),
    customerParty: party ? { code: text(party.code, `${at}.customerParty.code`), name: nullableText(party.name, `${at}.customerParty.name`) } : null,
    sandbox: bool(row.sandbox, `${at}.sandbox`), status: oneOf(row.status, ["active", "revoked"], `${at}.status`),
    rateLimitPerMinute: number(row.rateLimitPerMinute, `${at}.rateLimitPerMinute`), expiresAt: nullableText(row.expiresAt, `${at}.expiresAt`),
    createdAt: text(row.createdAt, `${at}.createdAt`), createdBy: text(row.createdBy, `${at}.createdBy`), secretHint: text(row.secretHint, `${at}.secretHint`),
    secretRotatedAt: nullableText(row.secretRotatedAt, `${at}.secretRotatedAt`), revokedAt: nullableText(row.revokedAt, `${at}.revokedAt`),
    revokedBy: nullableText(row.revokedBy, `${at}.revokedBy`), lastUsedAt: nullableText(row.lastUsedAt, `${at}.lastUsedAt`) };
};
const clientPage = (value: unknown): ApiClientPage => {
  const page = object(value, "API client page"), summary = object(page.summary, "API client summary");
  return { items: list(page.items, "API client page.items", client), next: nullableText(page.next, "API client page.next"), summary: {
    active: number(summary.active, "API client summary.active"), sandbox: number(summary.sandbox, "API client summary.sandbox"),
    revoked: number(summary.revoked, "API client summary.revoked"), secretsOlderThanOneYear: number(summary.secretsOlderThanOneYear, "API client summary.secretsOlderThanOneYear"),
    lastUsedAt: nullableText(summary.lastUsedAt, "API client summary.lastUsedAt"),
  } };
};

const filters = (value: unknown, at: string): WebhookFilters => { const row = object(value, at); return {
  itemFamilies: stringList(row.itemFamilies, `${at}.itemFamilies`), customerPartyRefs: stringList(row.customerPartyRefs, `${at}.customerPartyRefs`),
  legalEntityIds: stringList(row.legalEntityIds, `${at}.legalEntityIds`),
}; };
const webhook = (value: unknown, at = "webhook"): WebhookSubscription => {
  const row = object(value, at), activity = object(row.activity, `${at}.activity`);
  return { id: text(row.id, `${at}.id`), name: text(row.name, `${at}.name`), url: text(row.url, `${at}.url`), eventTypes: stringList(row.eventTypes, `${at}.eventTypes`),
    filters: filters(row.filters, `${at}.filters`), status: oneOf(row.status, ["pending_verification", "active", "paused", "disabled"], `${at}.status`), relayId: nullableText(row.relayId, `${at}.relayId`),
    ratePerSecond: number(row.ratePerSecond, `${at}.ratePerSecond`), verifiedAt: nullableText(row.verifiedAt, `${at}.verifiedAt`), failingSince: nullableText(row.failingSince, `${at}.failingSince`),
    pausedAt: nullableText(row.pausedAt, `${at}.pausedAt`), pausedReason: nullableText(row.pausedReason, `${at}.pausedReason`), createdAt: text(row.createdAt, `${at}.createdAt`),
    updatedAt: text(row.updatedAt, `${at}.updatedAt`), version: text(row.version, `${at}.version`), previousSecretExpiresAt: nullableText(row.previousSecretExpiresAt, `${at}.previousSecretExpiresAt`),
    activity: { deliveredLast24Hours: number(activity.deliveredLast24Hours, `${at}.activity.deliveredLast24Hours`), totalLast24Hours: number(activity.totalLast24Hours, `${at}.activity.totalLast24Hours`), parked: number(activity.parked, `${at}.activity.parked`) } };
};
const webhookPage = (value: unknown): WebhookPage => { const page = object(value, "webhook page"), summary = object(page.summary, "webhook summary"); return {
  items: list(page.items, "webhook page.items", webhook), nextCursor: nullableText(page.nextCursor, "webhook page.nextCursor"), summary: {
    deliveredLast24Hours: number(summary.deliveredLast24Hours, "webhook summary.deliveredLast24Hours"), waitingToRetry: number(summary.waitingToRetry, "webhook summary.waitingToRetry"),
    parked: number(summary.parked, "webhook summary.parked"), pausedEndpoints: number(summary.pausedEndpoints, "webhook summary.pausedEndpoints"),
  } };
};

const delivery = (value: unknown, at = "delivery"): WebhookDeliveryRow => { const row = object(value, at); return {
  id: text(row.id, `${at}.id`), eventId: text(row.eventId, `${at}.eventId`), eventType: text(row.eventType, `${at}.eventType`), status: oneOf(row.status, ["pending", "succeeded", "parked", "discarded"], `${at}.status`),
  attemptCount: number(row.attemptCount, `${at}.attemptCount`), createdAt: text(row.createdAt, `${at}.createdAt`), nextAttemptAt: text(row.nextAttemptAt, `${at}.nextAttemptAt`), lastAttemptAt: nullableText(row.lastAttemptAt, `${at}.lastAttemptAt`),
  succeededAt: nullableText(row.succeededAt, `${at}.succeededAt`), parkedAt: nullableText(row.parkedAt, `${at}.parkedAt`), discardedAt: nullableText(row.discardedAt, `${at}.discardedAt`), replayOf: nullableText(row.replayOf, `${at}.replayOf`),
  recordReference: nullableText(row.recordReference, `${at}.recordReference`), lastOutcome: nullableText(row.lastOutcome, `${at}.lastOutcome`), lastStatusCode: row.lastStatusCode === null ? null : number(row.lastStatusCode, `${at}.lastStatusCode`),
  lastDurationMs: row.lastDurationMs === null ? null : number(row.lastDurationMs, `${at}.lastDurationMs`), lastResponseExcerpt: nullableText(row.lastResponseExcerpt, `${at}.lastResponseExcerpt`), lastErrorCode: nullableText(row.lastErrorCode, `${at}.lastErrorCode`),
}; };
const deliveryPage = (value: unknown): DeliveryPage => { const page = object(value, "delivery page"), counts = object(page.counts, "delivery counts"); return {
  items: list(page.items, "delivery page.items", delivery), nextCursor: nullableText(page.nextCursor, "delivery page.nextCursor"), total: number(page.total, "delivery page.total"),
  counts: { all: number(counts.all, "delivery counts.all"), succeeded: number(counts.succeeded, "delivery counts.succeeded"), pending: number(counts.pending, "delivery counts.pending"), parked: number(counts.parked, "delivery counts.parked"), discarded: number(counts.discarded, "delivery counts.discarded") },
}; };
const attempt = (value: unknown, at: string): DeliveryAttempt => { const row = object(value, at); return {
  id: text(row.id, `${at}.id`), attemptedAt: text(row.attemptedAt, `${at}.attemptedAt`), outcome: text(row.outcome, `${at}.outcome`), statusCode: row.statusCode === null ? null : number(row.statusCode, `${at}.statusCode`),
  durationMs: number(row.durationMs, `${at}.durationMs`), responseExcerpt: nullableText(row.responseExcerpt, `${at}.responseExcerpt`), errorCode: nullableText(row.errorCode, `${at}.errorCode`),
}; };

export const developerApi = {
  clients: async (after?: string, signal?: AbortSignal) => clientPage(await api<unknown>(`/api/developer/clients?${q({ after, limit: 50 })}`, { signal })),
  client: async (id: string, signal?: AbortSignal) => client(await api<unknown>(`/api/developer/clients/${encodeURIComponent(id)}`, { signal })),
  createClient: async (request: CreateApiClientRequest) => { const row = object(await api<unknown>("/api/developer/clients", { method: "POST", body: request }), "created API client"); return { client: client(row.client), secret: text(row.secret, "created API client.secret") }; },
  updateClient: async (id: string, request: { scopes: string[]; rateLimitPerMinute: number }) => client(await api<unknown>(`/api/developer/clients/${encodeURIComponent(id)}`, { method: "PUT", body: request })),
  rotateClient: async (id: string) => { const row = object(await api<unknown>(`/api/developer/clients/${encodeURIComponent(id)}/rotate-secret`, { method: "POST" }), "rotated API client"); return { clientId: text(row.clientId, "rotated API client.clientId"), secret: text(row.secret, "rotated API client.secret"), previousSecretExpiresAt: text(row.previousSecretExpiresAt, "rotated API client.previousSecretExpiresAt") }; },
  revokeClient: async (id: string) => client(await api<unknown>(`/api/developer/clients/${encodeURIComponent(id)}/revoke`, { method: "POST" })),
  webhooks: async (after?: string, signal?: AbortSignal) => webhookPage(await api<unknown>(`/api/developer/webhooks?${q({ after, take: 50 })}`, { signal })),
  webhook: async (id: string, signal?: AbortSignal) => webhook(await api<unknown>(`/api/developer/webhooks/${encodeURIComponent(id)}`, { signal })),
  createWebhook: async (request: WebhookRequest) => { const row = object(await api<unknown>("/api/developer/webhooks", { method: "POST", body: request }), "created webhook"); return { subscription: webhook(row.subscription), secret: text(row.secret, "created webhook.secret") }; },
  updateWebhook: async (id: string, request: WebhookRequest & { expectedVersion: string }) => { const row = object(await api<unknown>(`/api/developer/webhooks/${encodeURIComponent(id)}`, { method: "PUT", body: request }), "updated webhook"); return webhook(object(row.subscription, "updated webhook.subscription")); },
  webhookAction: async (id: string, action: "pause" | "resume" | "verify") => { const row = object(await api<unknown>(`/api/developer/webhooks/${encodeURIComponent(id)}/${action}`, { method: "POST" }), `webhook ${action}`); return webhook(row.subscription); },
  rotateWebhook: async (id: string) => { const row = object(await api<unknown>(`/api/developer/webhooks/${encodeURIComponent(id)}/rotate-secret`, { method: "POST" }), "rotated webhook"); return { subscription: webhook(row.subscription), secret: text(row.secret, "rotated webhook.secret") }; },
  deleteWebhook: (id: string) => api<unknown>(`/api/developer/webhooks/${encodeURIComponent(id)}`, { method: "DELETE" }),
  testWebhook: (id: string, eventType?: string) => api<unknown>(`/api/developer/webhooks/${encodeURIComponent(id)}/test`, { method: "POST", body: { eventType: eventType ?? null } }),
  deliveries: async (id: string, filter: DeliveryFilters, cursor?: string, signal?: AbortSignal) => deliveryPage(await api<unknown>(`/api/developer/webhooks/${encodeURIComponent(id)}/deliveries?${q({ ...filter, cursor, take: 50 })}`, { signal })),
  delivery: async (id: string, deliveryId: string, signal?: AbortSignal) => { const row = object(await api<unknown>(`/api/developer/webhooks/${encodeURIComponent(id)}/deliveries/${encodeURIComponent(deliveryId)}`, { signal }), "delivery detail"); return { delivery: delivery(row.delivery), eventBody: text(row.eventBody, "delivery detail.eventBody"), attempts: list(row.attempts, "delivery detail.attempts", attempt) }; },
  redeliver: (id: string, deliveryId: string) => api<unknown>(`/api/developer/webhooks/${encodeURIComponent(id)}/deliveries/${encodeURIComponent(deliveryId)}/redeliver`, { method: "POST" }),
  discard: (id: string, deliveryId: string) => api<unknown>(`/api/developer/webhooks/${encodeURIComponent(id)}/deliveries/${encodeURIComponent(deliveryId)}/discard`, { method: "POST" }),
  redeliverRange: (id: string, from: string, to: string) => api<{ created: number }>(`/api/developer/webhooks/${encodeURIComponent(id)}/deliveries/redeliver-range`, { method: "POST", body: { from, to } }),
  catalogue: async (signal?: AbortSignal): Promise<DeveloperCatalogue> => { const row = object(await api<unknown>("/api/developer/catalogue", { signal }), "developer catalogue"); return {
    events: list(row.events, "developer catalogue.events", (value, at) => { const entry = object(value, at); return { type: text(entry.type, `${at}.type`), version: text(entry.version, `${at}.version`), schema: object(entry.schema, `${at}.schema`), sample: object(entry.sample, `${at}.sample`) }; }), scopes: stringList(row.scopes, "developer catalogue.scopes"),
  }; },
  settings: async (signal?: AbortSignal): Promise<WebhookSettings> => { const row = object(await api<unknown>("/api/developer/webhooks/settings", { signal }), "webhook settings"); return { retentionDays: number(row.retentionDays, "webhook settings.retentionDays") }; },
  updateSettings: async (retentionDays: number): Promise<WebhookSettings> => { const row = object(await api<unknown>("/api/developer/webhooks/settings", { method: "PUT", body: { retentionDays } }), "webhook settings"); return { retentionDays: number(row.retentionDays, "webhook settings.retentionDays") }; },
  openApi: async (signal?: AbortSignal) => object(await api<unknown>("/api/public/v1/openapi.json", { signal }), "OpenAPI document"),
};
