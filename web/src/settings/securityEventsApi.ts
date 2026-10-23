import { api } from "../api/client.ts";

export interface SecurityEventRow {
  occurredAt: string;
  kind: string;
  userName: string | null;
  actorName: string | null;
  clientAddress: string | null;
  userAgent: string | null;
  reason: string;
}

export interface SecurityEventPage { items: SecurityEventRow[]; nextCursor: string | null }

export interface SecurityEventFilter {
  kind?: string;
  userId?: string;
  from?: string;
  to?: string;
  cursor?: string;
  take: number;
}

export function securityEventQuery(filter: SecurityEventFilter): string {
  const query = new URLSearchParams();
  Object.entries(filter).forEach(([key, value]) => {
    if (value !== undefined && value !== "") query.set(key, String(value));
  });
  return query.toString();
}

export const securityEventsApi = {
  page: (filter: SecurityEventFilter, signal?: AbortSignal) =>
    api<SecurityEventPage>(`/api/security/events?${securityEventQuery(filter)}`, { signal }),
};
