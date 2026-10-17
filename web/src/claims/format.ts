import type { Tone } from "../components";
import type { CurrencyInfo, MoneyTotal, PolicyStackStep } from "./types";

export const CLAIM_STATUSES = ["submitted", "adjudicating", "approved", "partially_approved", "denied", "settled", "closed"] as const;
export const COVERAGE_SOURCES = ["warranty", "recall", "goodwill", "contract", "chargeable"] as const;
export const LINE_KINDS = ["parts", "labour", "travel"] as const;
export const REMEDIES = ["repair", "replacement"] as const;
export const AGE_LABELS = ["Under 2 days", "2–5 days", "6–10 days", "11–20 days", "Over 20 days"] as const;

// ISO 3166-1 alpha-2 codes are data at the browser boundary. Names come from Intl.DisplayNames in the active locale.
export const COUNTRY_CODES = "AD AE AF AG AI AL AM AO AQ AR AS AT AU AW AX AZ BA BB BD BE BF BG BH BI BJ BL BM BN BO BQ BR BS BT BV BW BY BZ CA CC CD CF CG CH CI CK CL CM CN CO CR CU CV CW CX CY CZ DE DJ DK DM DO DZ EC EE EG EH ER ES ET FI FJ FK FM FO FR GA GB GD GE GF GG GH GI GL GM GN GP GQ GR GS GT GU GW GY HK HM HN HR HT HU ID IE IL IM IN IO IQ IR IS IT JE JM JO JP KE KG KH KI KM KN KP KR KW KY KZ LA LB LC LI LK LR LS LT LU LV LY MA MC MD ME MF MG MH MK ML MM MN MO MP MQ MR MS MT MU MV MW MX MY MZ NA NC NE NF NG NI NL NO NP NR NU NZ OM PA PE PF PG PH PK PL PM PN PR PS PT PW PY QA RE RO RS RU RW SA SB SC SD SE SG SH SI SJ SK SL SM SN SO SR SS ST SV SX SY SZ TC TD TF TG TH TJ TK TL TM TN TO TR TT TV TW TZ UA UG UM US UY UZ VA VC VE VG VI VN VU WF WS YE YT ZA ZM ZW".split(" ");

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

export function countryName(code: string): string {
  try { return new Intl.DisplayNames(undefined, { type: "region" }).of(code) ?? code; } catch { return code; }
}
