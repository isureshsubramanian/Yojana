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
  { path: "/entitlements", label: "Entitlement gaps", icon: "ent", section: "Work" },
  { path: "/entitlements/policies", label: "Warranty policies", icon: "set", section: "Work" },
  { path: "/entitlements/bulk", label: "Bulk entitlements", icon: "range", section: "Work" },
  { path: "/entitlements/exceptions", label: "Warranty-line exceptions", icon: "bell", section: "Work" },
  { path: "/approvals", label: "Approvals", icon: "check", section: "Work", roles: ["service_manager", "tenant_administrator", "serial_administrator", "production_supervisor", "inventory_controller", "order_desk_supervisor", "quality", "item_master_owner", "shipping_supervisor", "service_supervisor"] },
  { path: "/corrections", label: "Correction workbench", icon: "corr", section: "Work" },
  { path: "/claims", label: "Claims", icon: "claim", section: "Work" },
  { path: "/rma", label: "RMA", icon: "rma", section: "Work", placeholder: "RMA, replacement and refurbishment" },
  { path: "/certificates", label: "Certificates", icon: "cert", section: "Work", placeholder: "Certificates and labels" },
  { path: "/imports", label: "Imports", icon: "import", section: "Data", placeholder: "Imports and 3PL capture" },
  { path: "/integration", label: "Integration", icon: "int", section: "Data", placeholder: "Connections and the integration gateway" },
  { path: "/settings", label: "Settings", icon: "set", section: "Data", roles: ["tenant_administrator"], placeholder: "Tenant settings" },
  { path: "/settings/currencies", label: "Currencies", icon: "set", section: "Data", roles: ["tenant_administrator"] },
  { path: "/settings/workbench-choices", label: "Workbench choices", icon: "set", section: "Data", roles: ["tenant_administrator"] },
];

if (process.env.NODE_ENV !== "production") {
  navigation.push({ path: "/patterns", label: "Patterns", icon: "set", section: "Development" });
}

export function entryFor(pathname: string): NavEntry | undefined {
  const matches = navigation.filter((e) => e.path !== "/" && (pathname === e.path || pathname.startsWith(e.path + "/")));
  return matches.sort((a, b) => b.path.length - a.path.length)[0] ?? (pathname === "/" ? navigation[0] : undefined);
}
