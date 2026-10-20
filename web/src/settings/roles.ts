export const STAFF_ROLES = [
  "tenant_administrator", "serial_administrator", "service_desk", "service_supervisor", "service_manager",
  "shipping_supervisor", "production_supervisor", "item_master_owner", "quality", "inventory_controller",
  "order_desk_supervisor", "label_administrator", "certificate_administrator", "legal_reviewer", "finance_review",
  "technician", "customer_service", "warehouse_lead", "integration_operator",
] as const;

export const roleLabel = (role: string) => role.split("_").map((part, index) => index === 0 ? part[0]?.toUpperCase() + part.slice(1) : part).join(" ");
