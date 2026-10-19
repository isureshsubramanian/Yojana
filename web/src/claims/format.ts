import type { Tone } from "../components";
import type { CampaignCoverageSource, CampaignCoverReason, ClaimCampaignOption, CurrencyInfo, MoneyTotal, PolicyStackStep } from "./types";

export const CLAIM_STATUSES = ["submitted", "adjudicating", "approved", "partially_approved", "denied", "settled", "closed"] as const;
export const COVERAGE_SOURCES = ["warranty", "recall", "goodwill", "contract", "chargeable"] as const;
export const LINE_KINDS = ["parts", "labour", "travel"] as const;
export const REMEDIES = ["repair", "replacement"] as const;
export const AGE_LABELS = ["Under 2 days", "2–5 days", "6–10 days", "11–20 days", "Over 20 days"] as const;

export function humanise(value: string | null | undefined): string {
  if (!value) return "—";
  const text = value.replace(/_/g, " ").replace(/\s+/g, " ");
  return text[0]!.toUpperCase() + text.slice(1);
}

export function claimTone(status: string): Tone {
  if (["approved", "partially_approved", "settled", "closed", "covered", "matched", "ok"].includes(status)) return "ok";
  if (["denied", "fail", "out_of_region"].includes(status)) return "bad";
  if (["submitted", "chargeable", "no_match"].includes(status)) return "warn";
  if (status === "adjudicating") return "info";
  return "neutral";
}

export function stackOutcome(step: Pick<PolicyStackStep, "outcome">): string {
  switch (step.outcome) {
    case "matched": return "Matched";
    case "no_match": return "No match";
    case "not_reached": return "Not reached";
    case "out_of_region": return "Out of region";
    default: return humanise(step.outcome);
  }
}

export function minorDigits(code: string, catalogue: readonly CurrencyInfo[]): number {
  return catalogue.find((item) => item.code === code)?.minorDigits ?? 2;
}

export function money(amount: number, currency: string, catalogue: readonly CurrencyInfo[] = []): string {
  return new Intl.NumberFormat(undefined, { style: "currency", currency, minimumFractionDigits: minorDigits(currency, catalogue), maximumFractionDigits: minorDigits(currency, catalogue) }).format(amount);
}

export function moneyTotals(totals: readonly MoneyTotal[], catalogue: readonly CurrencyInfo[] = []): string {
  return totals.length ? totals.map((total) => money(total.amount, total.currency, catalogue)).join(" · ") : "—";
}

export function ageBucketLabel(index: number): string { return AGE_LABELS[index] ?? "Unknown"; }

export function evidenceCue(status: string, evidenceRequested: boolean, denialReason: string | null, recoveryRaised: boolean, linesDecided: number, lineCount: number): string {
  if (evidenceRequested) return "evidence requested";
  if (denialReason) return humanise(denialReason);
  if (recoveryRaised) return "recovery raised";
  if (status === "approved" || status === "partially_approved" || status === "denied") return "not settled";
  if (status === "adjudicating") return `lines ${linesDecided} of ${lineCount} decided`;
  return "awaiting adjudication";
}

export function csvCell(value: string | number | null | undefined): string {
  const text = value == null ? "" : String(value);
  return /[",\n]/.test(text) ? `"${text.replace(/"/g, '""')}"` : text;
}

export function checkLabel(key: string, value: string): string {
  switch (key) {
    case "overlay_flags": return "Overlay flags";
    case "service_country": return `Service country ${value}`;
    case "claim_date_in_term": return "Claim date inside the term";
    case "chain_length": return "Replacement chain length";
    case "item_service_status": return "Item service status";
    case "remedy": return "Remedy";
    case "consumer_choice": return "Consumer’s informed choice";
    default: return humanise(key);
  }
}

export function checkValue(key: string, value: string): string {
  if (key === "claim_date_in_term" && /^\d+\/\d+$/.test(value)) { const [month, term] = value.split("/"); return `Month ${month} of ${term}`; }
  if (key === "chain_length") return `${value} · threshold 3`;
  return humanise(value);
}

export function campaignOptionHint(options: readonly ClaimCampaignOption[], source: CampaignCoverageSource): string {
  const label = humanise(source);
  const forSource = options.filter((option) => option.coverageSource === source);
  if (forSource.some((option) => option.coversOnDate)) return `${label} cover is available through a campaign selecting this unit.`;
  const ended = forSource.find((option) => option.notCoveredReason === "campaign_ended");
  if (ended?.runsUntil) return `${ended.code} ended on ${ended.runsUntil}; a claim dated after it is decided under normal warranty.`;
  return `${label} needs an active campaign selecting this unit; none does.`;
}

export function campaignCoverTerms(option: Pick<ClaimCampaignOption, "partsPct" | "labourPct" | "travelPct">): string | null {
  if (option.partsPct === 100 && option.labourPct === 100 && option.travelPct === 100) return null;
  return `covers parts ${option.partsPct}% · labour ${option.labourPct}% · travel ${option.travelPct}%`;
}

export function campaignCoverNote(code: string, reason: CampaignCoverReason, runsUntil: string | null): string {
  const why = reason === "campaign_closed" ? "was closed" : runsUntil ? `ended on ${runsUntil}` : "had ended";
  return `Named campaign ${code} gave no cover: ${why} — lines decided under the policy stack.`;
}

export function coverageSourceLabel(source: string, campaignCode: string | null): string {
  return `${humanise(source)}${campaignCode ? ` · ${campaignCode}` : ""}`;
}

/** The unit's overlay flags arrive as the stored JSON array text; shown as words, "None" when clear. */
export function overlayFlagsText(raw: string | null | undefined): string {
  let flags: unknown = [];
  try { flags = JSON.parse(raw || "[]"); } catch { return raw || "None"; }
  return Array.isArray(flags) && flags.length > 0 ? flags.map((flag) => humanise(String(flag))).join(", ") : "None";
}
