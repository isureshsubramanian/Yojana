// Navigation registry. Later phases add their entries here (one line per screen task); the shell never
// knows a module's internals, only its route, label, icon and the role that may see it.
import type { IconName } from "./Icons";

export interface NavEntry {
  path: string;
  label: string;
  icon: IconName;
  /** Section heading the entry sits under; entries without one sit at the top. */
  section?: "Work" | "Data" | "Development";
  /** When set, the entry shows only to users holding one of these roles. */
  roles?: string[];
  /** Set when the module behind the entry is not part of this build yet. */
  placeholder?: string;
}

export const navigation: NavEntry[] = [
  { path: "/", label: "Overview", icon: "home" },
  { path: "/units", label: "Units", icon: "units", section: "Work" },
  { path: "/units/receive", label: "Receive and commission", icon: "import", section: "Work" },
  { path: "/units/ranges", label: "Serial ranges", icon: "range", section: "Work" },
  { path: "/units/completeness", label: "As-built completeness", icon: "tree", section: "Work" },
  { path: "/questions", label: "Questions", icon: "questions", section: "Work" },
  { path: "/entitlements", label: "Entitlement gaps", icon: "ent", section: "Work" },
  { path: "/entitlements/cover-changes", label: "Cover changes since sale", icon: "cover", section: "Work" },
  { path: "/entitlements/policies", label: "Warranty policies", icon: "policy", section: "Work" },
  { path: "/entitlements/bulk", label: "Bulk entitlements", icon: "layers", section: "Work" },
  { path: "/entitlements/exceptions", label: "Warranty-line exceptions", icon: "bell", section: "Work" },
  { path: "/approvals", label: "Approvals", icon: "check", section: "Work", roles: ["service_manager", "tenant_administrator", "serial_administrator", "production_supervisor", "inventory_controller", "order_desk_supervisor", "quality", "item_master_owner", "shipping_supervisor", "service_supervisor"] },
  { path: "/corrections", label: "Correction workbench", icon: "corr", section: "Work" },
  { path: "/claims", label: "Claims", icon: "claim", section: "Work" },
  { path: "/claims/campaigns", label: "Campaigns", icon: "campaign", section: "Work" },
  { path: "/claims/recovery", label: "Recovery claims", icon: "recover", section: "Work" },
  { path: "/returns", label: "Returns", icon: "returns", section: "Work" },
  { path: "/replacements/awaiting", label: "Replacements", icon: "swap", section: "Work" },
  { path: "/refurbishments", label: "Refurbishment", icon: "refurb", section: "Work" },
  // P6 certificates and labels.
  { path: "/certificates", label: "Certificates", icon: "cert", section: "Work" },
  { path: "/labels/print-jobs", label: "Labels", icon: "label", section: "Work" },
  { path: "/templates", label: "Templates", icon: "templates", section: "Data" },
  // End P6 certificates and labels.
  // P9-T20: the union of YojanaPolicy.CustomerMailDesigner and CustomerMailSender (mail/format.ts keeps the two lists).
  { path: "/mail", label: "Customer e-mail", icon: "mail", section: "Data", roles: ["tenant_administrator", "certificate_administrator", "service_manager", "customer_service", "service_desk", "service_supervisor"] },
  { path: "/lookup", label: "Lookup", icon: "lookup", section: "Data" },
  { path: "/imports", label: "Imports", icon: "upload", section: "Data" },
  { path: "/imports/history", label: "Upload history", icon: "scan", section: "Data", roles: ["serial_administrator", "production_supervisor", "quality", "inventory_controller", "warehouse_lead"] },
  { path: "/reconcile", label: "Reconcile", icon: "reconcile", section: "Data", roles: ["serial_administrator", "order_desk_supervisor"] },
  { path: "/reports/identifier-cross-reference", label: "Reports", icon: "report", section: "Data" },
  { path: "/integration", label: "Integration", icon: "int", section: "Data", roles: ["tenant_administrator", "integration_operator"] },
  // P6 OD-26 numbering settings navigation block.
  { path: "/settings", label: "Settings", icon: "set", section: "Data", roles: ["tenant_administrator"] },
  { path: "/settings/disclosure", label: "Disclosure tiers", icon: "disclosure", section: "Data", roles: ["tenant_administrator"] },
  // End P6 OD-26 numbering settings navigation block.
  { path: "/settings/currencies", label: "Currencies", icon: "coins", section: "Data", roles: ["tenant_administrator"] },
  { path: "/settings/recovery", label: "Supplier recovery", icon: "sliders", section: "Data", roles: ["tenant_administrator"] },
  { path: "/settings/refurbishment", label: "Refurbishment policy", icon: "cycle", section: "Data", roles: ["tenant_administrator"] },
  { path: "/settings/workbench-choices", label: "Workbench choices", icon: "list", section: "Data", roles: ["tenant_administrator"] },
  // P6 jurisdiction rules navigation block.
  { path: "/settings/jurisdictions", label: "Jurisdiction rules", icon: "globe", section: "Data", roles: ["tenant_administrator"] },
  // End P6 jurisdiction rules navigation block.
];

if (process.env.NODE_ENV !== "production") {
  navigation.push({ path: "/patterns", label: "Patterns", icon: "grid", section: "Development" });
}

export function entryFor(pathname: string): NavEntry | undefined {
  const matches = navigation.filter((e) => e.path !== "/" && (pathname === e.path || pathname.startsWith(e.path + "/")));
  return matches.sort((a, b) => b.path.length - a.path.length)[0] ?? (pathname === "/" ? navigation[0] : undefined);
}
