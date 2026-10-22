import type { Kpi, Tone } from "../components";
import type { GoLiveItemView, HandoverStepView, ParallelRunDashboard } from "./adminTypes";

const dateOnly = (value: string) => value.slice(0, 10);

export function parallelDate(value: string | null | undefined, includeYear = false): string {
  if (!value) return "not set";
  const parsed = new Date(`${dateOnly(value)}T00:00:00Z`);
  return new Intl.DateTimeFormat(undefined, { day: "numeric", month: "short", ...(includeYear ? { year: "numeric" as const } : {}), timeZone: "UTC" }).format(parsed);
}

export function ageInDays(value: string | null, now = Date.now()): number | null {
  if (!value) return null;
  return Math.max(0, Math.floor((now - new Date(value).getTime()) / 86_400_000));
}

export function lag(value: number | null): string {
  if (value === null) return "Unknown";
  if (value < 60) return `${value} s`;
  const minutes = Math.floor(value / 60); const seconds = value % 60;
  return seconds ? `${minutes} m ${seconds} s` : `${minutes} min`;
}

export function parallelEyebrow(run: ParallelRunDashboard): string {
  const owned = run.handover.filter(step => step.state === "module_owns").map(step => step.label.toLocaleLowerCase());
  const ownership = owned.length ? `the module owns ${owned.join(", ")}` : "handover not started";
  const day = run.dayNumber !== null && run.days !== null ? `day ${run.dayNumber} of ${run.days}` : "run dates not started";
  return `Cutover ${parallelDate(run.cutoverAt, true)} · ${day} · both systems live, ${ownership}`;
}

export function parallelKpis(run: ParallelRunDashboard, now = Date.now()): Kpi[] {
  const oldest = ageInDays(run.conflicts.oldestDetectedAt, now);
  const reconciliationDone = run.reconciliation.clean + run.reconciliation.explained;
  return [
    { label: "Days left", value: run.daysLeft?.toLocaleString() ?? "—", detail: `ends ${parallelDate(run.endDate)} · month-end close ${parallelDate(run.monthEndCloseOn)}` },
    { label: "Sync lag now", value: lag(run.syncLag.nowSeconds), detail: run.syncLag.messagesToday === 0 ? "no messages today" : `${run.syncLag.shareWithinTargetToday?.toLocaleString(undefined, { maximumFractionDigits: 1 }) ?? "—"}% within ${lag(run.syncLag.targetSeconds)} today` },
    { label: "Conflicts to review", value: run.conflicts.open.toLocaleString(), detail: `${oldest === null ? "oldest unknown" : `oldest ${oldest} day${oldest === 1 ? "" : "s"}`} · ${run.conflicts.autoResolved === 0 ? "none auto-resolved" : `${run.conflicts.autoResolved.toLocaleString()} auto-resolved`}` },
    { label: "Failed messages", value: run.failedMessages.open.toLocaleString(), detail: run.failedMessages.olderThanSevenDays === 0 ? "none older than 7 days" : `${run.failedMessages.olderThanSevenDays.toLocaleString()} older than 7 days` },
    { label: "Nightly reconciliation", value: `${reconciliationDone.toLocaleString()} of ${run.reconciliation.runs.toLocaleString()}`, detail: `clean · ${run.reconciliation.explained.toLocaleString()} explained${run.reconciliation.lastExplainedOn ? ` (${parallelDate(run.reconciliation.lastExplainedOn)})` : ""}` },
  ];
}

export function handoverState(step: HandoverStepView): string {
  if (step.state === "module_owns") return "Module owns";
  if (step.state === "next") return `Next${step.plannedOn ? ` · ${parallelDate(step.plannedOn)}` : ""}`;
  if (step.state === "after_close") return "After close";
  return "Planned";
}

export function handoverTone(step: HandoverStepView): Tone {
  return step.state === "module_owns" ? "ok" : step.state === "next" ? "info" : "neutral";
}

export function goLiveTone(item: GoLiveItemView): Tone {
  return item.status === "done" ? "ok" : item.status === "fail" ? "bad" : "neutral";
}

export function goLiveStatus(item: GoLiveItemView): string {
  return item.status === "done" ? "Done" : item.status === "fail" ? "Fail" : "Open";
}

export function conflictRemoteLabel(fieldName: string, remoteValue: string | null): string {
  const label = fieldName.replace(/([a-z0-9])([A-Z])/g, "$1 $2").replace(/_/g, " ").trim();
  const sentence = label ? label[0]!.toUpperCase() + label.slice(1) : "Value";
  return `${sentence} changed to ${remoteValue ?? "empty"}`;
}
