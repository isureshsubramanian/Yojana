import * as z from "zod/mini";
import { apiParsed } from "../api/client.ts";

export const securityEventRow = z.object({ occurredAt: z.string(), kind: z.string(), userName: z.nullable(z.string()),
  actorName: z.nullable(z.string()), clientAddress: z.nullable(z.string()), userAgent: z.nullable(z.string()),
  reason: z.string() });
export type SecurityEventRow = z.infer<typeof securityEventRow>;
export const securityEventPage = z.object({ items: z.array(securityEventRow), nextCursor: z.nullable(z.string()) });
export type SecurityEventPage = z.infer<typeof securityEventPage>;
export const securityEventSummary = z.object({ signIns: z.number(), failedSignIns: z.number(), lockouts: z.number(),
  passwordResets: z.number(), methodChanges: z.number() });
export type SecurityEventSummary = z.infer<typeof securityEventSummary>;

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
    apiParsed(`/api/security/events?${securityEventQuery(filter)}`, securityEventPage, { signal }),
  // Windows are tenant-day instants from tenantTime.dayBounds, so "today" matches the list's Today filter (ADR-0007).
  summary: (todayFrom: string, weekFrom: string, signal?: AbortSignal) =>
    apiParsed(`/api/security/events/summary?${new URLSearchParams({ todayFrom, weekFrom })}`, securityEventSummary, { signal }),
};
