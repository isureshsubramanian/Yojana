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
  { path: "/units", label: "Units", icon: "units", section: "Work", placeholder: "Unit identity, ledger and custody" },
  { path: "/entitlements", label: "Entitlements", icon: "ent", section: "Work", placeholder: "Warranty entitlements and policies" },
  { path: "/corrections", label: "Corrections", icon: "corr", section: "Work", placeholder: "Corrections and the serial workbench" },
  { path: "/claims", label: "Claims", icon: "claim", section: "Work", placeholder: "Claims, campaigns and supplier recovery" },
  { path: "/rma", label: "RMA", icon: "rma", section: "Work", placeholder: "RMA, replacement and refurbishment" },
  { path: "/certificates", label: "Certificates", icon: "cert", section: "Work", placeholder: "Certificates and labels" },
  { path: "/imports", label: "Imports", icon: "import", section: "Data", placeholder: "Imports and 3PL capture" },
  { path: "/integration", label: "Integration", icon: "int", section: "Data", placeholder: "Connections and the integration gateway" },
  { path: "/settings", label: "Settings", icon: "set", section: "Data", roles: ["tenant_administrator"], placeholder: "Tenant settings" },
];

if (process.env.NODE_ENV !== "production") {
  navigation.push({ path: "/patterns", label: "Patterns", icon: "set", section: "Development" });
}

export function entryFor(pathname: string): NavEntry | undefined {
  return navigation.find((e) => e.path !== "/" && (pathname === e.path || pathname.startsWith(e.path + "/"))) ?? (pathname === "/" ? navigation[0] : undefined);
}
