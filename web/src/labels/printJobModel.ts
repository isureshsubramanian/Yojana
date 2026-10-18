import type { Kpi } from "../components/types";
import type { LabelPrintJobSummary } from "./types";

export function summaryKpis(summary: LabelPrintJobSummary): Kpi[] {
  return [
    { label: "Printed", value: summary.printed.toLocaleString(), detail: `${summary.confirmed.toLocaleString()} confirmed by the printer` },
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
