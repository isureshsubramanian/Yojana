import type { Tone } from "../components";
import type { RefurbishmentReleaseCheck, RefurbishmentWorkOrderRow } from "./types";

export function humanise(value: string | null | undefined): string {
  if (!value) return "—";
  return value.replace(/[_.]/g, " ").replace(/^./, (letter) => letter.toUpperCase());
}
export function refurbishmentTone(status: string): Tone { return status === "released" ? "ok" : status === "cancelled" ? "bad" : "info"; }
export function operationTone(status: string): Tone { return status === "completed" ? "ok" : "warn"; }
export function money(amount: number, currency: string | null): string {
  if (!currency || currency === "XXX") return amount.toFixed(2);
  try { return new Intl.NumberFormat(undefined, { style: "currency", currency }).format(amount); }
  catch { return `${currency} ${amount.toFixed(2)}`; }
}
export function hours(value: number): string { return `${Number.isInteger(value) ? value.toFixed(1) : value} h`; }
export function identityTitle(identity: string): string {
  if (identity === "same_unit_same_serial") return "Same unit · same serial";
  if (identity === "same_unit_new_serial") return "Same unit · new serial with lineage";
  if (identity === "new_unit") return "New derived unit";
  return humanise(identity);
}
export function identityOutcome(identity: string, currentSerial: string | null, releasedSerial: string | null, released: boolean): { before: string; after: string; detail: string } {
  const before = currentSerial ?? "Serial unavailable";
  if (identity === "same_unit_same_serial") return { before, after: releasedSerial ?? before, detail: "The unit and serial stay the same." };
  if (identity === "same_unit_new_serial") return { before, after: releasedSerial ?? (released ? "Serial unavailable" : "Allocated by the server on release"), detail: "The new serial supersedes this one; unit history and entitlements stay attached." };
  return { before, after: releasedSerial ?? (released ? "Serial unavailable" : "A new serial and derived unit are created on release"), detail: "The server creates a derived unit and applies the configured entitlement policy." };
}
export function releaseBlocked(checks: readonly RefurbishmentReleaseCheck[]): boolean { return checks.some((check) => check.required && !check.passed); }
export function completedOperations(row: RefurbishmentWorkOrderRow): number { return row.operations.filter((operation) => operation.status === "completed").length; }
