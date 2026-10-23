// The shell knows screens only through this grouped route contract. Feature modules expose a path,
// label, icon and optional visibility/count metadata; the flyout never reaches into their state.
import type { IconName } from "./Icons";

export type NavCountSource = "questions" | "integration_attention";

export interface NavEntry {
  path: string;
  label: string;
  icon: IconName;
  roles?: string[];
  countSource?: NavCountSource;
  placeholder?: string;
}

export interface NavGroup {
  key: string;
  label: string;
  icon: IconName;
  items: NavEntry[];
  position?: "bottom";
}

const integrationRoles = ["tenant_administrator", "integration_operator"];

export const navigation: NavGroup[] = [
  { key: "overview", label: "Overview", icon: "home", items: [{ path: "/", label: "Overview", icon: "home" }] },
  { key: "units", label: "Units", icon: "units", items: [
    { path: "/units", label: "Units", icon: "units" },
    { path: "/units/receive", label: "Receive and commission", icon: "import" },
    { path: "/units/ranges", label: "Serial ranges", icon: "range" },
    { path: "/units/completeness", label: "As-built completeness", icon: "tree" },
    { path: "/lookup", label: "Lookup", icon: "lookup" },
  ] },
  { key: "warranty", label: "Warranty", icon: "ent", items: [
    { path: "/entitlements", label: "Entitlement gaps", icon: "ent" },
    { path: "/entitlements/cover-changes", label: "Cover changes since sale", icon: "cover" },
    { path: "/entitlements/policies", label: "Warranty policies", icon: "policy" },
    { path: "/entitlements/bulk", label: "Bulk entitlements", icon: "layers" },
    { path: "/entitlements/exceptions", label: "Warranty-line exceptions", icon: "bell" },
  ] },
  { key: "service", label: "Service", icon: "tool", items: [
    { path: "/claims", label: "Claims", icon: "claim" },
    { path: "/claims/risk-review", label: "Claim risk review", icon: "shield" },
    { path: "/claims/campaigns", label: "Campaigns", icon: "warn" },
    { path: "/claims/recovery", label: "Recovery claims", icon: "coins" },
    { path: "/returns", label: "Returns", icon: "rma" },
    { path: "/replacements/awaiting", label: "Replacements", icon: "swap" },
    { path: "/refurbishments", label: "Refurbishment", icon: "cycle" },
  ] },
  { key: "corrections", label: "Corrections", icon: "corr", items: [
    { path: "/approvals", label: "Approvals", icon: "check", roles: ["service_manager", "tenant_administrator", "serial_administrator", "production_supervisor", "inventory_controller", "order_desk_supervisor", "quality", "item_master_owner", "shipping_supervisor", "service_supervisor"] },
    { path: "/corrections", label: "Correction workbench", icon: "corr" },
    { path: "/corrections/kpi", label: "Correction measures", icon: "report" },
  ] },
  { key: "documents", label: "Documents", icon: "cert", items: [
    { path: "/certificates", label: "Certificates", icon: "cert" },
    { path: "/labels/print-jobs", label: "Labels and print jobs", icon: "print" },
    { path: "/templates", label: "Templates", icon: "file" },
    { path: "/mail", label: "Customer e-mail", icon: "mail", roles: ["tenant_administrator", "certificate_administrator", "service_manager", "customer_service", "service_desk", "service_supervisor"] },
  ] },
  { key: "data", label: "Data", icon: "db", items: [
    { path: "/imports", label: "Imports", icon: "upload" },
    { path: "/imports/history", label: "Upload history", icon: "scan", roles: ["serial_administrator", "production_supervisor", "quality", "inventory_controller", "warehouse_lead"] },
    { path: "/reconcile", label: "Reconcile customer lists", icon: "reconcile", roles: ["serial_administrator", "order_desk_supervisor"] },
    { path: "/reports/identifier-cross-reference", label: "Reports", icon: "report" },
  ] },
  { key: "integration", label: "Integration", icon: "int", items: [
    { path: "/integration", label: "Overview", icon: "int", roles: integrationRoles },
    { path: "/integration/connections", label: "Connections", icon: "key", roles: integrationRoles },
    { path: "/integration/messages-attention", label: "Messages needing attention", icon: "warn", roles: integrationRoles, countSource: "integration_attention" },
    { path: "/integration/manual-tasks", label: "Manual tasks", icon: "list", roles: integrationRoles },
    { path: "/integration/daily-check", label: "Daily check", icon: "check", roles: integrationRoles },
    { path: "/integration/history-loads", label: "History loads", icon: "down", roles: integrationRoles },
    { path: "/integration/erp-change", label: "ERP change", icon: "swap", roles: integrationRoles },
    { path: "/integration/migration", label: "Migration", icon: "db", roles: integrationRoles },
  ] },
  { key: "knowledge", label: "Knowledge", icon: "book", items: [
    { path: "/questions", label: "My questions", icon: "questions", countSource: "questions" },
    { path: "/knowledge", label: "Knowledge base", icon: "book" },
    { path: "/knowledge/entries/review", label: "Entries to review", icon: "eye" },
    { path: "/knowledge/training", label: "Training paths", icon: "users" },
    { path: "/knowledge/reports", label: "Knowledge reports", icon: "report" },
  ] },
  { key: "settings", label: "Settings", icon: "set", position: "bottom", items: [
    { path: "/settings", label: "Settings", icon: "set", roles: ["tenant_administrator"] },
  ] },
];

export const navigationItems = navigation.flatMap(group => group.items);

export function visibleNavigation(roles: readonly string[]): NavGroup[] {
  return navigation.map(group => ({ ...group, items: group.items.filter(entry => !entry.roles || entry.roles.some(role => roles.includes(role))) }))
    .filter(group => group.items.length > 0);
}

export function groupHasWaiting(group: NavGroup, counts: Readonly<Partial<Record<NavCountSource, number>>>): boolean {
  return group.items.some(item => item.countSource && (counts[item.countSource] ?? 0) > 0);
}

export function entryFor(pathname: string): NavEntry | undefined {
  const matches = navigationItems.filter(entry => entry.path !== "/" && (pathname === entry.path || pathname.startsWith(entry.path + "/")));
  return matches.sort((a, b) => b.path.length - a.path.length)[0] ?? (pathname === "/" ? navigationItems[0] : undefined);
}
