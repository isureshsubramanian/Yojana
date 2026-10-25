import * as z from "zod/mini";
import { apiParsed, checkedBody } from "../../api/client.ts";

// Settings › Developers API boundary (ADR-0019): every response is checked against its schema and every request body
// before it is sent; the types below are derived from the schemas so a type cannot promise what nothing checks.

const text = z.string();
const nullableText = z.nullable(z.string());
const count = z.number();

const disclosureTier = z.enum(["customer", "back_office"]);
const clientStatus = z.enum(["active", "revoked"]);
const webhookStatus = z.enum(["pending_verification", "active", "paused", "disabled"]);
const deliveryStatus = z.enum(["pending", "succeeded", "parked", "discarded"]);
export type DisclosureTier = z.infer<typeof disclosureTier>;
export type ClientStatus = z.infer<typeof clientStatus>;
export type WebhookStatus = z.infer<typeof webhookStatus>;
export type DeliveryStatus = z.infer<typeof deliveryStatus>;

const apiClientParty = z.object({ code: text, name: nullableText });
const apiClientView = z.object({
  id: text, name: text, scopes: z.array(text), disclosureTier, customerParty: z.nullable(apiClientParty),
  sandbox: z.boolean(), status: clientStatus, rateLimitPerMinute: count, expiresAt: nullableText, createdAt: text,
  createdBy: text, secretHint: text, secretRotatedAt: nullableText, revokedAt: nullableText,
  revokedBy: nullableText, lastUsedAt: nullableText,
});
const apiClientSummary = z.object({
  active: count, sandbox: count, revoked: count, secretsOlderThanOneYear: count, lastUsedAt: nullableText,
});
const apiClientPage = z.object({ items: z.array(apiClientView), next: nullableText, summary: apiClientSummary });
const apiClientCreated = z.object({ client: apiClientView, secret: text });
const apiClientSecretRotated = z.object({ clientId: text, secret: text, previousSecretExpiresAt: text });
const createApiClientRequest = z.object({
  name: text, scopes: z.array(text), disclosureTier, customerPartyId: nullableText,
  sandbox: z.boolean(), expiresAt: nullableText, rateLimitPerMinute: count,
});
const updateApiClientRequest = z.object({ scopes: z.array(text), rateLimitPerMinute: count });
export type ApiClientParty = z.infer<typeof apiClientParty>;
export type ApiClientView = z.infer<typeof apiClientView>;
export type ApiClientSummary = z.infer<typeof apiClientSummary>;
export type ApiClientPage = z.infer<typeof apiClientPage>;
export type ApiClientCreated = z.infer<typeof apiClientCreated>;
export type ApiClientSecretRotated = z.infer<typeof apiClientSecretRotated>;
export type CreateApiClientRequest = z.infer<typeof createApiClientRequest>;

const webhookFilters = z.object({ itemFamilies: z.array(text), customerPartyRefs: z.array(text), legalEntityIds: z.array(text) });
const webhookActivity = z.object({ deliveredLast24Hours: count, totalLast24Hours: count, parked: count });
const webhookSubscription = z.object({
  id: text, name: text, url: text, eventTypes: z.array(text), filters: webhookFilters, status: webhookStatus,
  relayId: nullableText, ratePerSecond: count, verifiedAt: nullableText, failingSince: nullableText,
  pausedAt: nullableText, pausedReason: nullableText, createdAt: text, updatedAt: text, version: text,
  previousSecretExpiresAt: nullableText, activity: webhookActivity,
});
const webhookSummary = z.object({ deliveredLast24Hours: count, waitingToRetry: count, parked: count, pausedEndpoints: count });
const webhookPage = z.object({ items: z.array(webhookSubscription), nextCursor: nullableText, summary: webhookSummary });
const webhookSecretResponse = z.object({ subscription: webhookSubscription, secret: text });
const webhookActionResponse = z.object({ subscription: webhookSubscription });
// Relay delivery is not offered in v1 (ADR-0018, owner 2026-10-25): the request always carries relayId null.
const webhookRequest = z.object({ name: text, url: text, eventTypes: z.array(text), filters: webhookFilters, relayId: z.null() });
const webhookUpdateRequest = z.extend(webhookRequest, { expectedVersion: text });
const sendTestResponse = z.object({ eventId: text, deliveryId: text });
export type WebhookFilters = z.infer<typeof webhookFilters>;
export type WebhookActivity = z.infer<typeof webhookActivity>;
export type WebhookSubscription = z.infer<typeof webhookSubscription>;
export type WebhookSummary = z.infer<typeof webhookSummary>;
export type WebhookPage = z.infer<typeof webhookPage>;
export type WebhookSecretResponse = z.infer<typeof webhookSecretResponse>;
export type WebhookActionResponse = z.infer<typeof webhookActionResponse>;
export type WebhookRequest = z.infer<typeof webhookRequest>;

const deliveryStatusCounts = z.object({ all: count, succeeded: count, pending: count, parked: count, discarded: count });
const webhookDeliveryRow = z.object({
  id: text, eventId: text, eventType: text, status: deliveryStatus, attemptCount: count, createdAt: text,
  nextAttemptAt: text, lastAttemptAt: nullableText, succeededAt: nullableText, parkedAt: nullableText,
  discardedAt: nullableText, replayOf: nullableText, recordReference: nullableText, lastOutcome: nullableText,
  lastStatusCode: z.nullable(count), lastDurationMs: z.nullable(count), lastResponseExcerpt: nullableText,
  lastErrorCode: nullableText,
});
const deliveryPage = z.object({ items: z.array(webhookDeliveryRow), nextCursor: nullableText, total: count, counts: deliveryStatusCounts });
const deliveryAttempt = z.object({
  id: text, attemptedAt: text, outcome: text, statusCode: z.nullable(count), durationMs: count,
  responseExcerpt: nullableText, errorCode: nullableText,
});
const deliveryDetail = z.object({ delivery: webhookDeliveryRow, eventBody: text, attempts: z.array(deliveryAttempt) });
const redeliverResponse = z.object({ deliveryId: text, replayOf: text });
const discardResponse = z.object({ deliveryId: text, status: text });
const redeliverRangeResponse = z.object({ created: count });
export type DeliveryStatusCounts = z.infer<typeof deliveryStatusCounts>;
export type WebhookDeliveryRow = z.infer<typeof webhookDeliveryRow>;
export type DeliveryPage = z.infer<typeof deliveryPage>;
export type DeliveryAttempt = z.infer<typeof deliveryAttempt>;
export type DeliveryDetail = z.infer<typeof deliveryDetail>;

const jsonObject = z.record(z.string(), z.unknown());
const catalogueEntry = z.object({ type: text, version: text, schema: jsonObject, sample: jsonObject });
const developerCatalogue = z.object({ events: z.array(catalogueEntry), scopes: z.array(text) });
const webhookSettings = z.object({ retentionDays: count });
export type CatalogueEntry = z.infer<typeof catalogueEntry>;
export type DeveloperCatalogue = z.infer<typeof developerCatalogue>;
export type WebhookSettings = z.infer<typeof webhookSettings>;

export interface DeliveryFilters { status?: DeliveryStatus; eventType?: string; from?: string; to?: string }

const q = (values: Record<string, string | number | undefined>) => {
  const result = new URLSearchParams();
  for (const [key, value] of Object.entries(values)) if (value !== undefined && value !== "") result.set(key, String(value));
  return result.toString();
};
const clientPath = (id: string) => `/api/developer/clients/${encodeURIComponent(id)}`;
const webhookPath = (id: string) => `/api/developer/webhooks/${encodeURIComponent(id)}`;

export const developerApi = {
  clients: (after?: string, signal?: AbortSignal) => apiParsed(`/api/developer/clients?${q({ after, limit: 50 })}`, apiClientPage, { signal }),
  client: (id: string, signal?: AbortSignal) => apiParsed(clientPath(id), apiClientView, { signal }),
  createClient: (request: CreateApiClientRequest) => apiParsed("/api/developer/clients", apiClientCreated, { method: "POST", body: checkedBody(createApiClientRequest, request) }),
  updateClient: (id: string, request: { scopes: string[]; rateLimitPerMinute: number }) => apiParsed(clientPath(id), apiClientView, { method: "PUT", body: checkedBody(updateApiClientRequest, request) }),
  rotateClient: (id: string) => apiParsed(`${clientPath(id)}/rotate-secret`, apiClientSecretRotated, { method: "POST" }),
  revokeClient: (id: string) => apiParsed(`${clientPath(id)}/revoke`, apiClientView, { method: "POST" }),
  webhooks: (after?: string, signal?: AbortSignal) => apiParsed(`/api/developer/webhooks?${q({ after, take: 50 })}`, webhookPage, { signal }),
  webhook: (id: string, signal?: AbortSignal) => apiParsed(webhookPath(id), webhookSubscription, { signal }),
  createWebhook: (request: WebhookRequest) => apiParsed("/api/developer/webhooks", webhookSecretResponse, { method: "POST", body: checkedBody(webhookRequest, request) }),
  updateWebhook: async (id: string, request: WebhookRequest & { expectedVersion: string }) =>
    (await apiParsed(webhookPath(id), webhookActionResponse, { method: "PUT", body: checkedBody(webhookUpdateRequest, request) })).subscription,
  webhookAction: async (id: string, action: "pause" | "resume" | "verify") =>
    (await apiParsed(`${webhookPath(id)}/${action}`, webhookActionResponse, { method: "POST" })).subscription,
  rotateWebhook: (id: string) => apiParsed(`${webhookPath(id)}/rotate-secret`, webhookSecretResponse, { method: "POST" }),
  deleteWebhook: (id: string) => apiParsed(webhookPath(id), webhookActionResponse, { method: "DELETE" }),
  testWebhook: (id: string, eventType?: string) => apiParsed(`${webhookPath(id)}/test`, sendTestResponse, { method: "POST", body: { eventType: eventType ?? null } }),
  deliveries: (id: string, filter: DeliveryFilters, cursor?: string, signal?: AbortSignal) =>
    apiParsed(`${webhookPath(id)}/deliveries?${q({ ...filter, cursor, take: 50 })}`, deliveryPage, { signal }),
  delivery: (id: string, deliveryId: string, signal?: AbortSignal) =>
    apiParsed(`${webhookPath(id)}/deliveries/${encodeURIComponent(deliveryId)}`, deliveryDetail, { signal }),
  redeliver: (id: string, deliveryId: string) => apiParsed(`${webhookPath(id)}/deliveries/${encodeURIComponent(deliveryId)}/redeliver`, redeliverResponse, { method: "POST" }),
  discard: (id: string, deliveryId: string) => apiParsed(`${webhookPath(id)}/deliveries/${encodeURIComponent(deliveryId)}/discard`, discardResponse, { method: "POST" }),
  redeliverRange: (id: string, from: string, to: string) => apiParsed(`${webhookPath(id)}/deliveries/redeliver-range`, redeliverRangeResponse, { method: "POST", body: { from, to } }),
  catalogue: (signal?: AbortSignal) => apiParsed("/api/developer/catalogue", developerCatalogue, { signal }),
  settings: (signal?: AbortSignal) => apiParsed("/api/developer/webhooks/settings", webhookSettings, { signal }),
  updateSettings: (retentionDays: number) => apiParsed("/api/developer/webhooks/settings", webhookSettings, { method: "PUT", body: { retentionDays } }),
  openApi: (signal?: AbortSignal) => apiParsed("/api/public/v1/openapi.json", jsonObject, { signal }),
};
