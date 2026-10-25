import * as z from "zod/mini";
// Wire shapes shared with the host (src/Web/Yojana.Web/Program.cs and the platform's shell endpoints).
// The server is the source of truth; the client renders what it receives and decides nothing (MOD-02 contract).

export const badge = z.enum(["ok", "warn", "bad", "info", "neutral"]);
export type Badge = z.infer<typeof badge>;
export const healthStatus = z.enum(["none", "ok", "warn", "bad"]);
export type HealthStatus = z.infer<typeof healthStatus>;
export const trend = z.enum(["up", "down", "flat"]);
export type Trend = z.infer<typeof trend>;
export const sessionUser = z.object({ id: z.string(), email: z.string(), displayName: z.string() });
export type SessionUser = z.infer<typeof sessionUser>;
export const sessionTenant = z.object({ code: z.string(), name: z.string() });
export type SessionTenant = z.infer<typeof sessionTenant>;
export const tenantChoice = z.object({ code: z.string(), name: z.string(), roles: z.array(z.string()) });
export type TenantChoice = z.infer<typeof tenantChoice>;
export const session = z.object({ user: sessionUser, tenant: z.nullable(sessionTenant), roles: z.array(z.string()),
  tenants: z.array(tenantChoice), passwordChangeRequired: z.boolean(), recoveryCodesLow: z.optional(z.boolean()) });
export type Session = z.infer<typeof session>;
export const overviewKpi = z.object({ label: z.string(), value: z.string(), detail: z.nullable(z.string()),
  trend: z.nullable(trend) });
export type OverviewKpi = z.infer<typeof overviewKpi>;
export const overviewColumn = z.object({ label: z.string(), numeric: z.boolean(), width: z.number() });
export type OverviewColumn = z.infer<typeof overviewColumn>;
export const overviewCell = z.object({ text: z.string(), sub: z.nullable(z.string()), badge: z.nullable(badge),
  mono: z.boolean() });
export type OverviewCell = z.infer<typeof overviewCell>;
export const overviewLink = z.object({ label: z.string(), href: z.string() });
export type OverviewLink = z.infer<typeof overviewLink>;
export const overviewPanel = z.object({ id: z.string(), title: z.string(), meta: z.nullable(z.string()),
  columns: z.array(overviewColumn), rows: z.array(z.array(overviewCell)), link: z.nullable(overviewLink),
  empty: z.nullable(z.string()) });
export type OverviewPanel = z.infer<typeof overviewPanel>;
export const railRow = z.object({ label: z.string(), value: z.nullable(z.string()), badge: z.nullable(badge),
  badgeText: z.nullable(z.string()), detail: z.nullable(z.string()) });
export type RailRow = z.infer<typeof railRow>;
export const railSection = z.object({ title: z.string(), rows: z.array(railRow) });
export type RailSection = z.infer<typeof railSection>;
export const overview = z.object({ generatedAt: z.string(), kpis: z.array(overviewKpi), panels: z.array(overviewPanel),
  rail: z.array(railSection) });
export type Overview = z.infer<typeof overview>;
export const health = z.object({ status: healthStatus, label: z.string(), detail: z.nullable(z.string()) });
export type Health = z.infer<typeof health>;

