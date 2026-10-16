import type { StoredApproval } from "./types";

const KEY = "yojana.entitlement-approvals.v1";

export function parseApprovals(raw: string | null): StoredApproval[] {
  if (!raw) return [];
  try {
    const value = JSON.parse(raw) as unknown;
    return Array.isArray(value) ? value.filter(isStoredApproval) : [];
  } catch { return []; }
}

function isStoredApproval(value: unknown): value is StoredApproval {
  if (!value || typeof value !== "object") return false;
  const row = value as Partial<StoredApproval>;
  return typeof row.id === "string" && typeof row.selectionId === "string" && typeof row.operation === "string" && Array.isArray(row.blocked) && typeof row.createdAt === "string";
}

export function upsertApproval(rows: StoredApproval[], next: StoredApproval): StoredApproval[] {
  return [next, ...rows.filter((row) => row.id !== next.id && !(row.selectionId === next.selectionId && row.operation === next.operation))].slice(0, 50);
}

export function approvalId(selectionId: string, operation: string): string { return `${selectionId}:${operation}`; }

export function loadApprovals(): StoredApproval[] {
  return typeof localStorage === "undefined" ? [] : parseApprovals(localStorage.getItem(KEY));
}

export function saveApproval(next: StoredApproval): void {
  if (typeof localStorage === "undefined") return;
  localStorage.setItem(KEY, JSON.stringify(upsertApproval(loadApprovals(), next)));
  window.dispatchEvent(new Event("yojana:entitlement-approvals"));
}

export function removeApproval(id: string): void {
  if (typeof localStorage === "undefined") return;
  localStorage.setItem(KEY, JSON.stringify(loadApprovals().filter((row) => row.id !== id)));
  window.dispatchEvent(new Event("yojana:entitlement-approvals"));
}
