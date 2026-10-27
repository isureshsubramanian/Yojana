import type { Kpi } from "../components/types";
import type { LabelPrintJobSummary } from "./types";

export function summaryKpis(summary: LabelPrintJobSummary): Kpi[] {
  return [
    { label: "Printed", value: summary.printed.toLocaleString(), detail: `${summary.downloaded.toLocaleString()} downloaded · ${summary.confirmed.toLocaleString()} printer-confirmed` },
    { label: "Waiting", value: summary.waiting.toLocaleString(), detail: "rendered or sent" },
    { label: "Rejected", value: summary.rejected.toLocaleString(), detail: "nothing sent" },
    { label: "Reprints", value: summary.reprints.toLocaleString(), detail: "stored payload re-sent" },
    { label: "From events", value: summary.fromEvents.toLocaleString(), detail: "replacement, swap or correction" },
  ];
}

export function dayBounds(fromDay: string, throughDay: string): { from: string; to: string } {
  const from = new Date(`${fromDay}T00:00:00`);
  const through = new Date(`${throughDay || fromDay}T00:00:00`);
  through.setDate(through.getDate() + 1);
  return { from: from.toISOString(), to: through.toISOString() };
}

/** The inclusive local calendar range used on first open: today and the preceding 29 days. */
export function defaultPrintJobDays(now = new Date()): { fromDay: string; throughDay: string } {
  const through = new Date(now.getFullYear(), now.getMonth(), now.getDate());
  const from = new Date(through); from.setDate(from.getDate() - 29);
  return { fromDay: localDay(from), throughDay: localDay(through) };
}

export function printJobRejectionSummary(emptyRequiredField: string | null | undefined,
  technicalReason: string | null | undefined): string {
  const parsedField = technicalReason?.match(/^([a-z0-9_.]+)\s+is empty(?:\s+for unit\b.*)?$/i)?.[1];
  const field = emptyRequiredField?.trim() || parsedField;
  if (!field) return "The label could not be rendered";
  const name = (field.split(".").at(-1) ?? field).replaceAll("_", " ");
  return `Required field ${name.replace(/^./, (letter) => letter.toUpperCase())} is empty`;
}

function localDay(value: Date): string {
  return `${value.getFullYear()}-${String(value.getMonth() + 1).padStart(2, "0")}-${String(value.getDate()).padStart(2, "0")}`;
}
