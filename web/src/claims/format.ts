import type { Tone } from "../components";
import type {
  CampaignCoverageSource, CampaignCoverReason, ClaimCampaignOption, ClaimRiskFlagStatus, ClaimRiskRulesPolicy,
  CurrencyInfo, MoneyTotal, PolicyStackStep,
} from "./types";

export const CLAIM_STATUSES = ["submitted", "adjudicating", "approved", "partially_approved", "denied", "settled", "closed"] as const;
export const COVERAGE_SOURCES = ["warranty", "recall", "goodwill", "contract", "chargeable"] as const;
export const LINE_KINDS = ["parts", "labour", "travel"] as const;
export const REMEDIES = ["repair", "replacement"] as const;
export const AGE_LABELS = ["Under 2 days", "2–5 days", "6–10 days", "11–20 days", "Over 20 days"] as const;

export const CLAIM_RISK_RULE_LABELS: Readonly<Record<string, string>> = {
  recent_cover_purchase: "Cover bought just before",
  repeat_unit_claims: "Repeat claims on the unit",
  repeat_fault: "Same fault again",
  claimant_rate: "High claim rate",
  channel_mismatch: "Not the owner or seller",
  recent_ownership_change: "Owner just changed",
  part_not_returned: "Part not returned",
  duplicate_open_claim: "Already on another open claim",
};

export const CLAIM_RISK_DECISION_LABELS: Readonly<Record<ClaimRiskFlagStatus, string>> = {
  open: "Open",
  cleared: "Cleared",
  confirmed: "Concern confirmed",
};

export function claimRiskRuleLabel(code: string): string { return CLAIM_RISK_RULE_LABELS[code] ?? humanise(code); }
export function claimRiskDecisionLabel(value: ClaimRiskFlagStatus): string { return CLAIM_RISK_DECISION_LABELS[value]; }

export function claimRiskThresholdLabel(rule: string, policy: ClaimRiskRulesPolicy | null): string {
  if (!policy) return "Current threshold unavailable";
  switch (rule) {
    case "recent_cover_purchase": return `Incident within ${policy.recentCoverPurchase.days} days of buying the covering add-on`;
    case "repeat_unit_claims": return `${policy.repeatUnitClaims.minimumEarlierClaims} earlier claims within ${policy.repeatUnitClaims.days} days`;
    case "repeat_fault": return `Same cause within ${policy.repeatFault.days} days`;
    case "claimant_rate": return `Over ${numberText(policy.claimantRate.rateMultiplier)}× tenant rate in ${policy.claimantRate.days} days · at least ${policy.claimantRate.minimumClaims} claims and ${policy.claimantRate.minimumUnits} units`;
    case "channel_mismatch": return "Claimant outside the unit’s recorded sale, ownership or custody chain";
    case "recent_ownership_change": return `Ownership started within ${policy.recentOwnershipChange.days} days before the incident`;
    case "part_not_returned": return "An earlier replacement part was overdue at the incident date";
    case "duplicate_open_claim": return "The same unit or part serial is on another open claim";
    default: return "Current threshold unavailable";
  }
}

/** Stored rule evidence becomes sentences; unrecognised fields are deliberately never rendered as JSON. */
export function claimRiskEvidence(rule: string, evidence: Record<string, unknown>, thresholds: Record<string, unknown>): string[] {
  switch (rule) {
    case "recent_cover_purchase": {
      const incident = text(evidence, "incident_date", "incidentDate");
      const days = integer(thresholds, "days");
      const lines = objects(evidence, "lines");
      if (!lines.length) return fallbackEvidence(rule);
      return lines.map((line) => {
        const family = humanise(text(line, "coverageFamily", "coverage_family"));
        const purchase = text(line, "purchaseDate", "purchase_date");
        const after = integer(line, "daysAfterPurchase", "days_after_purchase");
        return `${family || "Cover"} was bought ${dateText(purchase)}; the incident was ${dateText(incident)}${after === null ? "" : `, ${after} days later`}.${days === null ? "" : ` The rule threshold was ${days} days.`} The waiting-period denial did not apply to this line.`;
      });
    }
    case "repeat_unit_claims": {
      const incident = text(evidence, "incident_date", "incidentDate");
      const earlier = objects(evidence, "earlier_claims", "earlierClaims");
      const minimum = integer(thresholds, "minimum_earlier_claims", "minimumEarlierClaims");
      const days = integer(thresholds, "days");
      if (!earlier.length) return fallbackEvidence(rule);
      const claims = earlier.map((row) => `${text(row, "claimNumber", "claim_number") || "Earlier claim"} (${dateText(text(row, "incidentDate", "incident_date"))}${text(row, "status") ? `, ${humanise(text(row, "status")).toLowerCase()}` : ""})`).join(", ");
      return [`${earlier.length} earlier ${plural(earlier.length, "claim")} before the ${dateText(incident)} incident: ${claims}.${minimum === null || days === null ? "" : ` The rule threshold was ${minimum} within ${days} days.`}`];
    }
    case "repeat_fault": {
      const cause = humanise(text(evidence, "reported_cause_code", "reportedCauseCode"));
      const earlier = objects(evidence, "earlier_claims", "earlierClaims");
      const days = integer(thresholds, "days");
      if (!earlier.length) return fallbackEvidence(rule);
      const claims = earlier.map((row) => `${text(row, "claimNumber", "claim_number") || "Earlier claim"} (${dateText(text(row, "incidentDate", "incident_date"))})`).join(", ");
      return [`The same${cause ? ` ${cause.toLowerCase()}` : " reported"} fault appeared on ${claims}.${days === null ? "" : ` The rule window was ${days} days.`}`];
    }
    case "claimant_rate": {
      const partyClaims = integer(evidence, "party_claims", "partyClaims");
      const partyUnits = integer(evidence, "party_units", "partyUnits");
      const partyRate = decimal(evidence, "party_rate", "partyRate");
      const tenantClaims = integer(evidence, "tenant_claims", "tenantClaims");
      const tenantUnits = integer(evidence, "tenant_units", "tenantUnits");
      const tenantRate = decimal(evidence, "tenant_rate", "tenantRate");
      const multiplier = decimal(thresholds, "rate_multiplier", "rateMultiplier");
      const days = integer(thresholds, "days");
      if ([partyClaims, partyUnits, partyRate, tenantClaims, tenantUnits, tenantRate].some((value) => value === null)) return fallbackEvidence(rule);
      const ratio = tenantRate! > 0 ? partyRate! / tenantRate! : null;
      return [`The claimant had ${partyClaims} ${plural(partyClaims!, "claim")} across ${partyUnits} units (${percent(partyRate!)})${days === null ? "" : ` in ${days} days`}; the tenant baseline was ${tenantClaims} across ${tenantUnits} units (${percent(tenantRate!)}).${ratio === null ? "" : ` That is ${numberText(ratio)}× the tenant rate.`}${multiplier === null ? "" : ` The rule threshold was over ${numberText(multiplier)}×.`}`];
    }
    case "channel_mismatch":
      return ["The claimant was not the recorded owner or custodian and did not appear in the unit’s recorded party history."];
    case "recent_ownership_change": {
      const incident = text(evidence, "incident_date", "incidentDate");
      const ownership = object(evidence, "ownership");
      const opened = ownership ? text(ownership, "openedAt", "opened_at") : "";
      const days = integer(thresholds, "days");
      if (!opened) return fallbackEvidence(rule);
      const elapsed = daysBetween(opened, incident);
      return [`Ownership started ${dateText(opened)}${elapsed === null ? "" : `, ${elapsed} days before the ${dateText(incident)} incident`}.${days === null ? "" : ` The rule threshold was ${days} days.`}`];
    }
    case "part_not_returned": {
      const returns = objects(evidence, "outstanding_returns", "outstandingReturns");
      if (!returns.length) return fallbackEvidence(rule);
      return returns.map((row) => {
        const serial = text(row, "part_serial", "partSerial") || "Serial unavailable";
        const source = text(row, "sourceNumber", "source_number");
        const due = text(row, "returnDue", "return_due");
        return `Replacement part ${serial}${source ? ` from ${source}` : ""} was due back ${dateText(due)} and was still outstanding at the incident date.`;
      });
    }
    case "duplicate_open_claim": {
      const claims = objects(evidence, "claims");
      if (!claims.length) return fallbackEvidence(rule);
      return claims.map((row) => {
        const serials = objects(row, "serials").map((entry) => `${humanise(text(entry, "kind")).toLowerCase() || "unit"} ${text(entry, "serial") || "serial unavailable"}`).join(", ");
        return `${text(row, "claimNumber", "claim_number") || "Another claim"} is ${humanise(text(row, "status")).toLowerCase() || "open"}${serials ? ` and already includes ${serials}` : ""}.`;
      });
    }
    default: return fallbackEvidence(rule);
  }
}

function fallbackEvidence(rule: string): string[] { return [`${claimRiskRuleLabel(rule)} matched the stored claim facts; no displayable evidence fields were returned.`]; }
function object(value: Record<string, unknown>, ...keys: string[]): Record<string, unknown> | null {
  for (const key of keys) { const candidate = value[key]; if (candidate && typeof candidate === "object" && !Array.isArray(candidate)) return candidate as Record<string, unknown>; }
  return null;
}
function objects(value: Record<string, unknown>, ...keys: string[]): Record<string, unknown>[] {
  for (const key of keys) { const candidate = value[key]; if (Array.isArray(candidate)) return candidate.filter((entry): entry is Record<string, unknown> => !!entry && typeof entry === "object" && !Array.isArray(entry)); }
  return [];
}
function text(value: Record<string, unknown>, ...keys: string[]): string {
  for (const key of keys) { const candidate = value[key]; if (typeof candidate === "string") return candidate; }
  return "";
}
function decimal(value: Record<string, unknown>, ...keys: string[]): number | null {
  for (const key of keys) { const candidate = value[key]; if (typeof candidate === "number" && Number.isFinite(candidate)) return candidate; }
  return null;
}
function integer(value: Record<string, unknown>, ...keys: string[]): number | null {
  const candidate = decimal(value, ...keys); return candidate === null ? null : Math.trunc(candidate);
}
function dateText(value: string): string { return /^\d{4}-\d{2}-\d{2}/.test(value) ? value.slice(0, 10) : value || "an unrecorded date"; }
function daysBetween(from: string, to: string): number | null {
  const start = Date.parse(from.slice(0, 10) + "T00:00:00Z"); const end = Date.parse(to.slice(0, 10) + "T00:00:00Z");
  return Number.isFinite(start) && Number.isFinite(end) ? Math.round((end - start) / 86400000) : null;
}
function numberText(value: number): string { return Number.isInteger(value) ? value.toLocaleString() : value.toLocaleString(undefined, { maximumFractionDigits: 2 }); }
function percent(value: number): string { return new Intl.NumberFormat(undefined, { style: "percent", maximumFractionDigits: 1 }).format(value); }
function plural(count: number, singular: string): string { return count === 1 ? singular : `${singular}s`; }

export function humanise(value: string | null | undefined): string {
  if (!value) return "—";
  const text = value.replace(/_/g, " ").replace(/\s+/g, " ");
  return text[0]!.toUpperCase() + text.slice(1);
}

const denialReasonLabels: Record<string, string> = {
  claim_waiting_period: "Within the waiting period",
};

export function denialReasonLabel(reason: string | null | undefined, purchaseDate?: string | null, firstClaimableDate?: string | null): string {
  if (!reason) return "—";
  if (reason === "claim_waiting_period" && purchaseDate && firstClaimableDate)
    return `Within the waiting period: bought ${purchaseDate}, claimable from ${firstClaimableDate}`;
  return denialReasonLabels[reason] ?? humanise(reason);
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
