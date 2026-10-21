// Wire shapes shared with the host (src/Web/Yojana.Web/Program.cs and the platform's shell endpoints).
// The server is the source of truth; the client renders what it receives and decides nothing (MOD-02 contract).

export interface SessionUser { id: string; email: string; displayName: string }
export interface SessionTenant { code: string; name: string }
export interface TenantChoice { code: string; name: string; roles: string[] }
export interface Session {
  user: SessionUser;
  tenant: SessionTenant | null;
  roles: string[];
  tenants: TenantChoice[];
  passwordChangeRequired: boolean;
  recoveryCodesLow?: boolean;
}

export type Badge = "ok" | "warn" | "bad" | "info" | "neutral";
export type Trend = "up" | "down" | "flat";

export interface OverviewKpi { label: string; value: string; detail: string | null; trend: Trend | null }
export interface OverviewColumn { label: string; numeric: boolean; width: number }
export interface OverviewCell { text: string; sub: string | null; badge: Badge | null; mono: boolean }
export interface OverviewLink { label: string; href: string }
export interface OverviewPanel {
  id: string;
  title: string;
  meta: string | null;
  columns: OverviewColumn[];
  rows: OverviewCell[][];
  link: OverviewLink | null;
  empty: string | null;
}
export interface RailRow { label: string; value: string | null; badge: Badge | null; badgeText: string | null; detail: string | null }
export interface RailSection { title: string; rows: RailRow[] }
export interface Overview { generatedAt: string; kpis: OverviewKpi[]; panels: OverviewPanel[]; rail: RailSection[] }

export type HealthStatus = "none" | "ok" | "warn" | "bad";
export interface Health { status: HealthStatus; label: string; detail: string | null }
