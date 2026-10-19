import type { DeadLetterDetail, DeadLetterRow, ManualDraft, ManualTask } from "./types";

export function mayOperateIntegration(roles: readonly string[]): boolean {
  return roles.includes("tenant_administrator") || roles.includes("integration_operator");
}

export function maySeeIntegrationIndicator(roles: readonly string[]): boolean {
  return mayOperateIntegration(roles) || roles.includes("service_manager");
}

const reasonLabels: Record<string, string> = {
  item_not_mapped: "Unknown item", unknown_item: "Unknown item", serial_not_found: "Serial not found",
  party_not_mapped: "Customer or supplier not linked", party_not_linked: "Customer or supplier not linked",
  max_outbox_age: "Could not send", rejected: "Could not send", unsupported: "Needs hand entry",
};
const recordLabels: Record<string, string> = {
  DeliveryPosted: "Delivery", GoodsReceiptPosted: "Goods receipt", InvoicePosted: "Invoice",
  ProductionCompleted: "Production completion", CreateCreditMemo: "Credit note",
  CreateReplacementDelivery: "Replacement delivery", PostWarrantyExpense: "Warranty expense",
  CreateServiceOrder: "Service order", AttachDocument: "Attach document",
  ChangeStockStatusOrLocation: "Stock change",
};

export function human(value: string): string {
  const spaced = value.replace(/([a-z0-9])([A-Z])/g, "$1 $2").replace(/_/g, " ").trim();
  return spaced ? spaced.charAt(0).toUpperCase() + spaced.slice(1).toLowerCase() : value;
}
export const reasonLabel = (value: string) => reasonLabels[value] ?? human(value);
export const recordLabel = (value: string) => recordLabels[value] ?? human(value);
export const statusLabel = (value: string) => value === "needs_attention" ? "Open" : value === "retry_pending" ? "Retried" : human(value);

export function localDate(value: string): string {
  return new Intl.DateTimeFormat(undefined, { month: "2-digit", day: "2-digit", hour: "2-digit", minute: "2-digit" }).format(new Date(value));
}
export function age(value: string, now = Date.now()): string {
  const minutes = Math.max(0, Math.round((now - new Date(value).getTime()) / 60_000));
  if (minutes < 60) return `${minutes} min`;
  if (minutes < 48 * 60) return `${Math.round(minutes / 60)} h`;
  return `${Math.round(minutes / 1440)} d`;
}

function json(text: string): Record<string, unknown> {
  try { const value = JSON.parse(text) as unknown; return value && typeof value === "object" && !Array.isArray(value) ? value as Record<string, unknown> : {}; }
  catch { return {}; }
}
export const payload = (row: Pick<DeadLetterDetail, "rawPayload">) => json(row.rawPayload);
export const attempt = (row: Pick<DeadLetterDetail, "canonicalAttempt">) => json(row.canonicalAttempt);

export function recordTitle(row: Pick<DeadLetterRow, "recordType"> & { recordReference?: string | null; rawPayload?: string }): string {
  const source = row.rawPayload ? json(row.rawPayload) : {};
  const number = row.recordReference ?? source.documentNumber ?? source.document_number ?? source.reference ?? source.fileNumber;
  return `${recordLabel(row.recordType)}${typeof number === "string" || typeof number === "number" ? ` ${number}` : ""}`;
}

export function actionSeed(detail: DeadLetterDetail): { entityType: string; externalId: string; moduleId: string | null; moduleName: string | null } {
  const received = payload(detail); const tried = attempt(detail);
  const externalId = String(tried.externalId ?? tried.external_id ?? received.itemCode ?? received.item_code ?? received.customerCode ?? received.customer_code ?? "");
  const entityType = String(tried.entityType ?? tried.entity_type ?? (detail.reasonCode.includes("party") ? "party" : "item"));
  const moduleId = typeof tried.suggestedModuleId === "string" ? tried.suggestedModuleId : typeof tried.suggested_module_id === "string" ? tried.suggested_module_id : null;
  const moduleName = typeof tried.suggestedModuleName === "string" ? tried.suggestedModuleName : typeof tried.suggested_module_name === "string" ? tried.suggested_module_name : null;
  return { entityType, externalId, moduleId, moduleName };
}

export function parseDraft(task: Pick<ManualTask, "draft" | "commandType">): ManualDraft {
  const parsed = json(task.draft) as ManualDraft;
  return { title: parsed.title ?? recordLabel(task.commandType), ...parsed };
}

export function displayValue(value: unknown): string {
  if (value === null || value === undefined || value === "") return "—";
  if (typeof value === "string" || typeof value === "number" || typeof value === "boolean") return String(value);
  return JSON.stringify(value);
}
