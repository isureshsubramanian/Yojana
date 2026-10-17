import type { RmaDetailLine } from "./types";

export const RMA_STAGES = ["requested", "authorised", "in_transit", "received", "dispositioned", "closed"] as const;
export const CONDITION_GRADES = ["new", "open_box", "refurbished_a", "refurbished_b", "refurbished_c", "used", "remanufactured", "defective", "scrap"] as const;
export const REPLACEMENT_REASONS = ["warranty_defect", "dead_on_arrival", "seller_error", "goodwill", "recall"] as const;
export const PRICE_POLICIES = ["zero", "difference", "full"] as const;

export function humanise(value: string | null | undefined): string { return value ? value.replace(/_/g, " ").replace(/^./, (letter) => letter.toUpperCase()) : "—"; }
export function stageNumber(stage: string): number { const index = RMA_STAGES.indexOf(stage as typeof RMA_STAGES[number]); return index < 0 ? 0 : index + 1; }
export function ageBucket(ageDays: number): "fresh" | "watch" | "aged" { return ageDays <= 2 ? "fresh" : ageDays <= 7 ? "watch" : "aged"; }
export function ageBucketLabel(ageDays: number): string { return ageBucket(ageDays) === "fresh" ? "0–2 days" : ageBucket(ageDays) === "watch" ? "3–7 days" : "8+ days"; }
export function stageTone(stage: string): "ok" | "warn" | "info" | "neutral" { return stage === "closed" ? "neutral" : stage === "received" ? "warn" : stage === "dispositioned" ? "ok" : "info"; }
export function receiptTone(outcome: string | null): "ok" | "warn" | "bad" | "neutral" { return outcome === "accepted" ? "ok" : outcome === "unauthorised" ? "warn" : outcome ? "bad" : "neutral"; }

export interface OptionState { enabled: boolean; reason: string | null }
export function dispositionOptionState(disposition: string, line: RmaDetailLine, stage: string): OptionState {
  if (stage !== "received") return { enabled: false, reason: "Available after the return is received" };
  if (!line.receivedUnitId) return { enabled: false, reason: "Resolve the provisional identity first" };
  if (["mismatch", "unauthorised", "counterfeit"].includes(line.receiptOutcome ?? "")) return { enabled: false, reason: `Resolve the ${humanise(line.receiptOutcome).toLowerCase()} exception first` };
  if (disposition === "replace" && line.replacementIntentId) return { enabled: false, reason: "A replacement is already linked" };
  if (disposition === "restock_as_new" && !["new", "open_box"].includes(line.conditionGrade ?? "")) return { enabled: false, reason: "Only unused inspected stock can return as new" };
  return { enabled: true, reason: null };
}

export function gradeOptionState(grade: string): OptionState {
  if (["refurbished_a", "refurbished_b", "refurbished_c", "remanufactured"].includes(grade)) return { enabled: false, reason: "Set by a refurbishment release" };
  return { enabled: true, reason: null };
}
export function valuationRuleForGrade(grade: string): string {
  if (grade === "defective") return "write_down_to_salvage";
  if (grade === "scrap") return "scrap_value";
  if (grade === "new" || grade === "open_box") return "standard_cost";
  return "condition_adjusted";
}
export function receiptValuationRule(line: Pick<RmaDetailLine, "receiptBusinessTime" | "valuationRule">, grade: string): { rule: string; proposed: boolean } {
  if (line.receiptBusinessTime && line.valuationRule) return { rule: line.valuationRule, proposed: false };
  return { rule: valuationRuleForGrade(grade), proposed: true };
}
export function reasonEffect(reason: string): string {
  if (reason === "warranty_defect") return "replacement policy sets the end";
  if (reason === "dead_on_arrival") return "start and term kept · new units only";
  if (reason === "seller_error") return "start and term kept · not a warranty cost";
  if (reason === "goodwill") return "policy named on the approval";
  return "policy named on the campaign";
}
export function priceLabel(policy: string): string { return policy === "zero" ? "No charge" : humanise(policy); }
export function isToday(value: string | null, today = new Date().toISOString().slice(0, 10)): boolean { return value?.slice(0, 10) === today; }
