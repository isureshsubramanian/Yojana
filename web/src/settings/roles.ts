export const STAFF_ROLES = [
  "tenant_administrator", "serial_administrator", "service_desk", "service_supervisor", "service_manager",
  "shipping_supervisor", "production_supervisor", "item_master_owner", "quality", "inventory_controller",
  "order_desk_supervisor", "label_administrator", "certificate_administrator", "legal_reviewer", "finance_review",
  "technician", "customer_service", "warehouse_lead", "integration_operator",
  "migration_lead",
] as const;

/** Groups for role pickers; every STAFF_ROLES entry sits in exactly one group (checked by test/users.test.ts). */
export const ROLE_GROUPS: { label: string; roles: readonly (typeof STAFF_ROLES)[number][] }[] = [
  { label: "Administration", roles: ["tenant_administrator", "serial_administrator", "label_administrator", "certificate_administrator", "integration_operator", "migration_lead"] },
  { label: "Service", roles: ["service_desk", "service_supervisor", "service_manager", "technician", "customer_service"] },
  { label: "Operations", roles: ["shipping_supervisor", "production_supervisor", "warehouse_lead", "inventory_controller", "item_master_owner", "order_desk_supervisor"] },
  { label: "Quality, legal and finance", roles: ["quality", "legal_reviewer", "finance_review"] },
];

const ROLE_LABELS: Record<string, string> = {
  migration_lead: "Migration lead",
};

export const roleLabel = (role: string) => ROLE_LABELS[role] ?? role.split("_").map((part, index) => index === 0 ? part[0]?.toUpperCase() + part.slice(1) : part).join(" ");
