import type { Tone } from "../components/types";
import type { RecoveryClaimRow, RecoveryCurrencyTotal, RecoveryNotRaisedRow } from "./types";

export function money(amount: number, currency: string, fractionDigits = 2): string {
  try { return new Intl.NumberFormat(undefined, { style: "currency", currency, minimumFractionDigits: fractionDigits, maximumFractionDigits: fractionDigits }).format(amount); }
  catch { return `${currency} ${amount.toLocaleString(undefined, { minimumFractionDigits: fractionDigits, maximumFractionDigits: fractionDigits })}`; }
}

export function currencyAmounts(rows: readonly RecoveryCurrencyTotal[], field: "requestedAmount" | "approvedAmount"): { value: string; others: string } {
  const [primary, ...others] = rows;
  return {
    value: primary ? money(primary[field], primary.currency, 0) : "—",
    others: others.map((row) => money(row[field], row.currency, 0)).join(" · "),
  };
}

export function primaryApprovalRate(rows: readonly RecoveryCurrencyTotal[]): number | null {
  const primary = rows[0];
  return primary && primary.requestedAmount > 0 ? Math.round(primary.approvedAmount * 100 / primary.requestedAmount) : null;
}

export function recoveryState(row: Pick<RecoveryClaimRow, "status" | "ageDays" | "approvedAmount" | "requestedAmount" | "vendorNote">): { label: string; tone: Tone; detail: string } {
  if (row.status === "approved") {
    if (row.approvedAmount === 0) return { label: "Refused", tone: "bad", detail: row.vendorNote ?? "vendor declined" };
    if (row.approvedAmount !== null && row.approvedAmount < row.requestedAmount) return { label: "Partially approved", tone: "ok", detail: row.vendorNote ?? "below requested" };
    return { label: "Approved", tone: "ok", detail: row.vendorNote ?? "vendor answer recorded" };
  }
  return row.ageDays >= 7 ? { label: "Waiting", tone: "warn", detail: `${row.ageDays} d` } : { label: "Raised", tone: "info", detail: `${row.ageDays} d` };
}

export function termText(months: number | null, end: string | null): string {
  const duration = months === null ? "Term" : `${months} m`;
  return end ? `${duration} · ends ${end.slice(0, 10)}` : duration;
}

export function noRecoveryExplanation(row: RecoveryNotRaisedRow): string {
  switch (row.reason) {
    case "recall_claim": return "recall claim · recovery handled once for the whole campaign";
    case "vendor_term_ended": return `vendor term ${row.vendorTermMonths ?? "—"} m ended ${row.vendorTermEnd?.slice(0, 10) ?? "—"} · claim in month ${row.claimMonthOfTerm ?? "—"}`;
    default: return "made in house · no inbound entitlement on the unit or a child";
  }
}
