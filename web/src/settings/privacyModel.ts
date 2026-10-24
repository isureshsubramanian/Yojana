import type { ErasureBlocker, ErasureDetail, ErasurePreview, ErasureStatus, ReviewAction } from "./privacyApi.ts";

export function erasureStatus(status: ErasureStatus, failureRule: string | null): { label: string; tone: "ok" | "info" | "warn" | "bad" | "neutral" } {
  if (status === "completed") return { label: "Completed", tone: "ok" };
  if (status === "running") return { label: "In progress", tone: "info" };
  if (status === "pending") return { label: "Waiting to start", tone: "neutral" };
  if (status === "failed") return { label: "Needs attention", tone: "bad" };
  const reason = failureRule === "erasure_open_obligations" ? "open business" : failureRule?.replace(/^erasure_/, "").replace(/_/g, " ");
  return { label: `Refused${reason ? ` · ${reason}` : ""}`, tone: "warn" };
}

export function canConfirm(preview: ErasurePreview | null, decisions: Record<string, ReviewAction | undefined>, requestReference: string, confirmation: string): boolean {
  return preview !== null && preview.reviewItems.every(item => decisions[item.itemKey] === "Redact" || decisions[item.itemKey] === "Keep")
    && requestReference.length > 0 && confirmation === requestReference;
}

export function blockerPath(blocker: ErasureBlocker): string | null {
  if (!blocker.recordId) return null;
  if (blocker.kind === "claim") return `/claims/${blocker.recordId}`;
  if (blocker.kind === "rma") return `/returns/${blocker.recordId}`;
  if (blocker.kind === "replacement") return `/replacements/${blocker.recordId}`;
  return null;
}

export interface ErasureStep { label: string; detail: string; status: "Done" | "Running" | "Waiting" | "Refused" }

export function erasureSteps(detail: ErasureDetail): ErasureStep[] {
  if (detail.status === "refused") return [{ label: "Request checked", detail: detail.failureDetail ?? "The request was refused.", status: "Refused" }];
  const stores = (names: string[]) => detail.counts.filter(row => names.includes(row.store));
  const step = (label: string, names: string[], waiting: string): ErasureStep => {
    const rows = stores(names);
    const done = rows.length > 0 && rows.every(row => row.status === "completed");
    const running = rows.some(row => row.status === "completed") || detail.status === "running" && rows.some(row => row.status === "previewed");
    const count = rows.reduce((total, row) => total + (row.status === "completed" ? row.erasedCount : row.previewCount), 0);
    return { label, detail: rows.length ? `${count.toLocaleString()} matching records` : waiting, status: done ? "Done" : running ? "Running" : "Waiting" };
  };
  return [
    { label: "1 · Stop source updates restoring the name", detail: "recorded before anything else", status: "Done" },
    step("2 · Users and mail", ["platform"], "waiting for account and mail checks"),
    step("3 · Integration copies", ["integration"], "waiting for integration copies"),
    step("4 · Units, certificates and notes", ["serialisation", "knowledge"], "waiting for serial and knowledge records"),
  ];
}

export function storeLabel(store: string, kind: string): string {
  const labels: Record<string, string> = {
    customer_mail: "Customer emails", portal_attachments: "Portal attachments", unit_event_party_code: "Unit history",
    certificates: "Warranty certificates", imports: "Import files", free_text: "Free text",
    reference_projection: "Source copy of the customer", stored_payloads: "Stored integration messages",
    external_id_map: "External identifiers", user: "User account", secrets: "Sign-in data", invitations: "Invitations",
    security_events: "Security events", staff_mail: "Staff mail",
  };
  return labels[kind] ?? `${store.charAt(0).toUpperCase()}${store.slice(1)} · ${kind.replace(/_/g, " ")}`;
}
