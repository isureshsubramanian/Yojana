import type { ApiClientView, DeliveryStatus, WebhookDeliveryRow, WebhookStatus } from "./developerApi.ts";

export const scopeLabels: Readonly<Record<string, string>> = {
  "units.read": "Units · read serials, identifiers, lineage and as-built",
  "cover.read": "Cover · read warranties and what each covers",
  "claims.read": "Claims · read",
  "claims.write": "Claims · submit",
  "rma.read": "RMAs · read",
  "rma.write": "RMAs · request",
  "certificates.read": "Certificates · read records and PDFs",
  "events.read": "Events · look up a webhook event by its id",
  "units.write-installation": "Units · set installation date",
  "units.write-asset-tag": "Units · add customer asset tag",
};

export const scopeDescription = (scope: string) => scopeLabels[scope] ?? scope.replace(/[._-]/g, " ");
export const shortClientId = (value: string) => value.length > 18 ? `${value.slice(0, 8)}…${value.slice(-6)}` : value;
export const when = (value: string | null) => value ? new Date(value).toLocaleString() : "Never";
export const relative = (value: string | null) => {
  if (!value) return "Never used";
  const minutes = Math.round((Date.now() - new Date(value).getTime()) / 60_000);
  if (minutes < 2) return "Just now";
  if (minutes < 60) return `${minutes} min ago`;
  if (minutes < 1_440) return `${Math.floor(minutes / 60)} h ago`;
  return new Date(value).toLocaleDateString();
};
export const customerLabel = (client: ApiClientView) => client.customerParty
  ? `${client.customerParty.name ?? client.customerParty.code} only` : "All customers";
export const clientSees = (client: ApiClientView) => client.sandbox ? "Sandbox · test events only"
  : `${client.disclosureTier === "customer" ? "Customer" : "Back office"} tier · ${customerLabel(client)}`;
export const clientStatus = (client: ApiClientView): { label: string; tone: "ok" | "info" | "warn" | "neutral" } => {
  if (client.status === "revoked") return { label: `Revoked${client.revokedAt ? ` · ${new Date(client.revokedAt).toLocaleDateString()}` : ""}`, tone: "neutral" };
  if (client.expiresAt && new Date(client.expiresAt).getTime() <= Date.now()) return { label: "Expired", tone: "warn" };
  if (client.sandbox) return { label: "Sandbox", tone: "info" };
  return { label: "Active", tone: "ok" };
};

export const webhookStatus = (status: WebhookStatus, failingSince: string | null): { label: string; tone: "ok" | "warn" | "bad" | "neutral" } => {
  if (status === "paused") return { label: `Paused${failingSince ? ` · failing since ${new Date(failingSince).toLocaleDateString()}` : ""}`, tone: "bad" };
  if (status === "pending_verification") return { label: "Waiting for verification", tone: "warn" };
  if (status === "disabled") return { label: "Deleted", tone: "neutral" };
  return { label: "Active", tone: "ok" };
};
export const deliveryStatus = (row: WebhookDeliveryRow): { label: string; tone: "ok" | "warn" | "bad" | "neutral" } => {
  if (row.status === "succeeded") return { label: "Delivered", tone: "ok" };
  if (row.status === "parked") return { label: "Parked", tone: "bad" };
  if (row.status === "discarded") return { label: "Discarded", tone: "neutral" };
  return { label: `Retrying${row.nextAttemptAt ? ` · next ${new Date(row.nextAttemptAt).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })}` : ""}`, tone: "warn" };
};
export const statusFilter = (segment: string): DeliveryStatus | undefined => segment === "delivered" ? "succeeded"
  : segment === "retrying" ? "pending" : segment === "parked" ? "parked" : segment === "discarded" ? "discarded" : undefined;
export const prettyJson = (value: string | Record<string, unknown>) => {
  try { return JSON.stringify(typeof value === "string" ? JSON.parse(value) as unknown : value, null, 2); }
  catch { return typeof value === "string" ? value : JSON.stringify(value, null, 2); }
};
export const answer = (row: WebhookDeliveryRow) => row.lastStatusCode ? `${row.lastStatusCode}${row.lastOutcome ? ` · ${row.lastOutcome}` : ""}`
  : row.lastErrorCode ?? row.lastOutcome ?? "Not attempted";
