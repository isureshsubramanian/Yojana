import type { Kpi, Tone } from "../components";
import type {
  ConnectionAuthorisationView, CutoverChecklistItemView, ErpChangeConnectionView, ErpChangeDivergenceRow, ErpChangeRunbookView,
  MasterDataExceptionRow, SignInHistoryItem, StockDifferenceRow, ErpChangeMyWorkView,
} from "./erpChangeTypes";
import { human } from "./model.ts";

// Mirrors the ERP change policies in PlatformSetup.cs (commented there too). The server enforces them; the client only hides
// what would be refused, so a policy change there needs the same change here.
export interface ErpChangeRights { read: boolean; control: boolean; masterData: boolean; documentDivergences: boolean; stock: boolean }
export function erpChangeRights(roles: readonly string[]): ErpChangeRights {
  const has = (role: string) => roles.includes(role);
  return {
    read: has("tenant_administrator") || has("integration_operator") || has("item_master_owner") || has("inventory_controller"),
    control: has("tenant_administrator") || has("integration_operator"),
    masterData: has("integration_operator") || has("item_master_owner"),
    documentDivergences: has("integration_operator"),
    stock: has("inventory_controller"),
  };
}

export const stepTitles = ["Register the new ERP", "Re-map master data", "Reconcile stock by unit", "Dual run", "Cut over outbound", "Archive the old ERP"] as const;
export const exitCriteria = [
  "New connection healthy, no outbound enabled",
  "Every item and party with live units has a mapping or a decided exception",
  "No open difference on a unit with live cover; the rest signed off",
  "Daily reconciliation shows no new divergences for the agreed period",
  "Every parked outbound message resolved on the new ERP or a manual task",
  "The old ERP is read-only history; one active connection",
] as const;

const n = (value: number) => value.toLocaleString();

export interface StepSummary { step: number; label: string; value: string; detail: string; tone: Tone; current: boolean }
export function stepSummaries(run: ErpChangeRunbookView): StepSummary[] {
  const done = (step: number) => run.currentStep > step || (run.status === "completed" && step <= 6);
  const dual = run.dualRun;
  const values: [string, string][] = [
    [done(1) ? "Done" : run.target.health === "healthy" ? "Ready" : human(run.target.health), `${human(run.target.health).toLocaleLowerCase()} · ${run.target.readOnly ? "read-only" : "read-write"} · outbound ${outboundLabel(run.target.outboundMode)}`],
    [done(2) ? "Done" : n(run.openMasterDataExceptions), done(2) ? "master data mapped" : "exceptions open"],
    [done(3) ? "Done" : n(run.openDifferences), done(3) ? `${n(run.resolvedDifferences)} resolved by command` : `differences open · ${n(run.coveredOpenDifferences)} with live cover`],
    [done(4) ? "Done" : dual?.startedAt ? `Day ${n(dual.elapsedDays)}` : "—", dual?.startedAt ? `${n(dual.consecutiveCleanDays)} of ${n(dual.cleanDaysRequired)} clean days · ${n(dual.minimumDays)} days minimum` : "30 days minimum"],
    [done(5) ? "Done" : run.cutoverChecklist ? `${run.cutoverChecklist.filter(item => item.state === "passed").length} of ${run.cutoverChecklist.length}` : "—", run.cutoverChecklist ? "checklist items passed" : "after the dual run"],
    [run.archive?.sourceArchived ? "Done" : "—", "history stays readable"],
  ];
  return stepTitles.map((title, index) => {
    const step = index + 1; const [value, detail] = values[index]!;
    const current = run.status === "active" && run.currentStep === step;
    const tone: Tone = value === "Done" ? "ok" : current && /^\d/.test(value) && value !== "0" ? "bad" : "neutral";
    return { step, label: `${step} · ${title}`, value, detail, tone, current };
  });
}

export interface ExitCriterion { step: number; label: string; detail: string; state: "passed" | "open" | "not_started" }
export function exitCriteriaStates(run: ErpChangeRunbookView): ExitCriterion[] {
  return exitCriteria.map((label, index) => {
    const step = index + 1;
    const check = [...run.checks].filter(value => value.step === step).sort((left, right) => right.checkedAt.localeCompare(left.checkedAt))[0];
    const state = run.currentStep > step || (check?.passed ?? false) ? "passed" : run.currentStep === step ? "open" : "not_started";
    return { step, label: `${step} · ${label}`, detail: check ? `${check.detail} · checked by ${check.checkedBy}` : state === "not_started" ? "Opens when the step before passes" : "Not checked yet", state };
  });
}
export const criterionTone = (state: ExitCriterion["state"]): Tone => state === "passed" ? "ok" : state === "open" ? "warn" : "neutral";
export const criterionLabel = (state: ExitCriterion["state"]) => state === "passed" ? "Passed" : state === "open" ? "Open" : "Not started";

export function outboundLabel(mode: string): string {
  return ({ none: "none", reference_only: "references only", documents: "documents" } as Record<string, string>)[mode] ?? human(mode).toLocaleLowerCase();
}
export function connectionLine(connection: ErpChangeConnectionView): string {
  return `${connection.readOnly ? "read-only" : "read-write"} · outbound: ${outboundLabel(connection.outboundMode)} · ${human(connection.health).toLocaleLowerCase()}`;
}

export function runbookKpis(run: ErpChangeRunbookView): Kpi[] {
  return [
    { label: "Units in stock compared", value: n(run.unitsCompared), detail: "module · current ERP · new ERP" },
    { label: "Differences open", value: n(run.openDifferences), detail: `${n(run.coveredOpenDifferences)} on units with live cover block step 3` },
    { label: "Resolved by command", value: n(run.resolvedDifferences), detail: "every one an ordinary unit event" },
    { label: "Master data exceptions", value: n(run.openMasterDataExceptions), detail: "open · no automatic merge" },
    { label: "Unit history changed by the ERP change", value: n(run.unitHistoriesChanged), detail: run.unitHistoryCheckedAt ? "unexpected · expected 0" : "not checked yet" },
  ];
}

// Roll back before cutover cancels (the new connection is paused, OD-67); after step 5 only a new change back is offered.
export const canCancel = (run: ErpChangeRunbookView) => run.status === "active" && run.currentStep < 5;
export const canReverse = (run: ErpChangeRunbookView) => run.status === "active" && run.currentStep >= 5;

export function erpChangeWorkSummary(work: ErpChangeMyWorkView): string {
  if (work.currentStep === 2) return `${work.masterDataExceptions.toLocaleString()} master-data exception${work.masterDataExceptions === 1 ? "" : "s"} need your decision`;
  if (work.currentStep === 3) return `${work.coveredStockDifferences.toLocaleString()} covered-unit difference${work.coveredStockDifferences === 1 ? "" : "s"} block it; ${work.otherStockDifferencesAwaitingSignOff.toLocaleString()} other${work.otherStockDifferencesAwaitingSignOff === 1 ? "" : "s"} need your sign-off`;
  return `${work.stockTypeDivergences.toLocaleString()} stock-type divergence${work.stockTypeDivergences === 1 ? "" : "s"} need your review`;
}

/** Roles the server lets the notification setting mail (ErpChangeNotifications.cs): the non-operator ErpChangeRead roles. */
export const notificationRecipientRoles = [
  ["item_master_owner", "Item master owners"],
  ["inventory_controller", "Inventory controllers"],
] as const;

const checklistLabels: Record<string, [string, string]> = {
  freeze_source: ["Stop outbound on the current ERP", "no new credit notes, journals or deliveries are sent there"],
  drain_outbox: ["Drain the queue", "every queued message is sent, or turned into a manual task with its reason"],
  export_pending_actions: ["Export open returns and claims with ERP actions pending", "CSV for the finance team"],
  enable_target: ["Switch the new ERP to read-write", "outbound mode as the tenant chooses"],
  reconcile_after_cutover: ["Run stock reconciliation once more", "must come back with no new differences"],
  first_outbound_confirmed: ["Confirm the first documents on the new ERP", "at least one recorded confirmation"],
};
export function checklistLabel(item: Pick<CutoverChecklistItemView, "code">): [string, string] {
  const known = checklistLabels[item.code]; if (known) return known;
  const [prefix, commandType] = item.code.split(":");
  if (prefix === "validate" && commandType) return [`Check ${commandName(commandType)} on the new ERP`, "test company run or a recorded manual confirmation"];
  if (prefix === "first_outbound" && commandType) return [`First ${commandName(commandType)} confirmed`, "document reference from the new ERP"];
  return [human(item.code), ""];
}
const commandNames: Record<string, string> = {
  CreateCreditMemo: "credit note", PostWarrantyExpense: "warranty journal", CreateReplacementDelivery: "replacement delivery",
  ChangeStockStatusOrLocation: "stock status change", CreateServiceOrder: "service order", AttachDocument: "attachment",
};
export const commandName = (value: string) => commandNames[value] ?? human(value).toLocaleLowerCase();
export const checklistTone = (state: string): Tone => state === "passed" ? "ok" : state === "failed" ? "bad" : "neutral";
export const checklistState = (state: string) => state === "passed" ? "Passed" : state === "failed" ? "Failed" : "Waiting";
export const validationCommand = (code: string) => code.startsWith("validate:") ? code.slice("validate:".length) : null;

export function divergenceReviewer(row: Pick<ErpChangeDivergenceRow, "kind">): string {
  return row.kind === "stock" ? "Inventory controller" : "Integration operator";
}
export function divergenceState(row: Pick<ErpChangeDivergenceRow, "status" | "resolution">): string {
  if (row.status !== "resolved") return "Open";
  return ({ accepted_source: "Current ERP kept", explained: "Explained", correction_raised: "Correction raised" } as Record<string, string>)[row.resolution ?? ""] ?? "Resolved";
}
export const mayDecideDivergence = (row: Pick<ErpChangeDivergenceRow, "kind">, rights: ErpChangeRights) => row.kind === "stock" ? rights.stock : rights.documentDivergences;

export function problemLabel(row: Pick<MasterDataExceptionRow, "problem" | "status">): string {
  const decided = row.status !== "open";
  if (row.problem === "ambiguous") return decided ? "Was ambiguous" : "Ambiguous";
  if (row.problem === "unmatched") return decided ? "Had no match" : "No match";
  return decided ? "Was missing" : "Missing in the new ERP";
}
export function exceptionDecision(row: MasterDataExceptionRow): string {
  if (row.status === "open") return "Open";
  const target = row.candidates.find(candidate => candidate.moduleId === row.resolvedModuleId)?.label;
  const by = row.resolvedBy ? ` · ${row.resolvedBy}` : "";
  return row.status === "not_needed" ? `Not needed${by}` : `Mapped${target ? ` to ${target}` : ""}${by}`;
}

const stockReasons: Record<string, string> = {
  missing_in_b: "Missing in new ERP", extra_in_b: "Extra in new ERP", location_differs: "Location differs", serial_differs_by_normalisation: "Serial format",
};
export const stockReasonLabel = (reason: string) => stockReasons[reason] ?? human(reason);
export function stockReasonTone(row: Pick<StockDifferenceRow, "reason" | "hasLiveEntitlement" | "status">): Tone {
  if (row.status === "resolved") return "ok";
  if (row.reason === "serial_differs_by_normalisation") return "info";
  return row.hasLiveEntitlement ? "warn" : "neutral";
}
const suggestions: Record<string, string> = {
  fix_in_new_erp: "Fix in the new ERP", none_same_after_normalising: "None · same unit after normalising",
  check_receive_or_ignore: "Check: receive or ignore", change_custody: "Change custody here",
};
export const suggestionLabel = (value: string) => suggestions[value] ?? human(value);
export const resolutionLabel = (value: string | null) => value === "correct_identifier" ? "Identifier corrected" : value === "merge_units" ? "Units merged" : value === "change_custody" ? "Custody changed" : value ? human(value) : "Resolved";
export const stockReasonFilters = [["", "All open"], ["missing_in_b", "Missing in the new ERP"], ["extra_in_b", "Extra in the new ERP"], ["location_differs", "Location differs"], ["serial_differs_by_normalisation", "Serial differs by format"]] as const;

// Sign-in of a delegated-token connection.
export function daysUntil(value: string | null, now = Date.now()): number | null {
  if (!value) return null;
  return Math.ceil((new Date(value).getTime() - now) / 86_400_000);
}
export function signInStatusLabel(view: Pick<ConnectionAuthorisationView, "status">): string {
  return ({ active: "Signed in", disabled: "Disabled", consent_pending: "Sign-in started", reconsent_required: "Sign in again needed" } as Record<string, string>)[view.status] ?? human(view.status);
}
export function signInNote(view: ConnectionAuthorisationView, now = Date.now()): { tone: "warn" | "bad" | "ok"; title: string } | null {
  if (view.status === "disabled") return { tone: "bad", title: "Disabled" };
  if (view.status === "reconsent_required") return { tone: "bad", title: "Sign in again needed" };
  const days = daysUntil(view.refreshChainExpiresAt, now);
  if (days === null) return null;
  const warnAt = Math.max(0, ...view.reminderDays);
  return days <= warnAt ? { tone: "warn", title: `Sign-in expires in ${days} day${days === 1 ? "" : "s"}` } : null;
}
export function expiryRuleLabel(view: Pick<ConnectionAuthorisationView, "refreshExpiryMode" | "refreshLifetimeDays">): string {
  return `${view.refreshExpiryMode === "absolute" ? "Fixed" : "Sliding"} ${view.refreshLifetimeDays} days`;
}
export function historyEvent(item: SignInHistoryItem): string {
  if (item.kind === "access_renewed") return item.count > 1 ? `Access renewed (${item.count} times)` : "Access renewed";
  if (item.kind === "expiry_reminder") return item.daysBeforeExpiry ? `Expiry reminder, ${item.daysBeforeExpiry} days` : "Expiry reminder";
  if (item.kind === "sign_in_required") return "Sync paused: sign in again needed";
  if (item.kind === "sync_paused") return "Tenant administrator told sync is paused";
  return ({ signed_in: "Signed in", disabled: "Disabled" } as Record<string, string>)[item.kind] ?? human(item.kind);
}
export function historyBy(item: SignInHistoryItem): string {
  if (item.kind === "expiry_reminder" && item.recipientRoles?.length) return `module, to ${item.recipientRoles.map(role => human(role).toLocaleLowerCase()).join(", ")}`;
  return item.by === "System" ? "module" : item.by;
}
export function historyResult(item: SignInHistoryItem): [string, Tone] {
  if (item.result === "tokens_deleted") return ["Tokens deleted", "bad"];
  if (item.result === "paused") return ["Paused", "warn"];
  if (item.result === "sent") return ["Sent", "warn"];
  return [human(item.result), item.result === "ok" ? "ok" : "neutral"];
}
export function parseReminderDays(text: string): number[] | null {
  const values = text.split(/[\s,]+/).filter(Boolean).map(Number);
  if (values.length < 1 || values.length > 3 || new Set(values).size !== values.length
    || values.some(value => !Number.isInteger(value) || value < 1 || value > 90)) return null;
  return [...values].sort((left, right) => right - left);
}
