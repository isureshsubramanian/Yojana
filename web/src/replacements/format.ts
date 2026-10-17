import type { Tone } from "../components";
import type { AwaitingFilter, ReplacementLinkKpis, ReplacementLinkRow } from "./types";

export function humanise(value: string | null | undefined): string {
  if (!value) return "—";
  return value.replace(/_/g, " ").replace(/^./, (letter) => letter.toUpperCase());
}

export function money(amount: number | null, currency: string): string {
  if (amount === null) return "Not set";
  try { return new Intl.NumberFormat(undefined, { style: "currency", currency }).format(amount); }
  catch { return `${currency} ${amount.toFixed(2)}`; }
}

export function replacementTone(row: ReplacementLinkRow): Tone {
  if (row.partyOnHold) return "bad";
  if (row.daysOverdue !== null) return "warn";
  return "info";
}

export function replacementStatus(row: ReplacementLinkRow): string {
  if (row.partyOnHold) return "Customer on hold";
  if (row.daysOverdue !== null) return "Overdue";
  return "Awaiting return";
}

export function dueText(row: ReplacementLinkRow): string {
  if (row.daysOverdue !== null) return `${row.daysOverdue} d overdue`;
  return row.returnDue ? `Due ${row.returnDue}` : "No due date";
}

export function filterTitle(filter: AwaitingFilter): string {
  return filter === "overdue" ? "Overdue" : filter === "due_soon" ? "Due in 7 days" : "All awaiting";
}

export function filterCount(filter: AwaitingFilter, kpis: ReplacementLinkKpis | null): number | null {
  if (!kpis) return null;
  return filter === "overdue" ? kpis.overdue : filter === "due_soon" ? kpis.dueInSevenDays : kpis.awaiting;
}
