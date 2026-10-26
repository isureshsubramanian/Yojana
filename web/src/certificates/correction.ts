// ADR-0023 viewer rules, kept free of React so they can be tested: who may correct or print a copy, when the actions
// are offered, and how a change row reads. The server enforces every rule again; these only shape the screen.
import type { CertificateCorrectionChange, CertificateDetail, CertificateListRow } from "./types.ts";

/** Mirrors YojanaPolicy.CertificateIssuer (PlatformSetup.cs). Change both together. */
export const CERTIFICATE_ISSUER_ROLES = ["quality", "certificate_administrator"] as const;
export const REASON_MINIMUM = 10;
export const REASON_MAXIMUM = 500;
const ROLE_NAMES = "the Quality or Certificate administrator role";

export function mayIssueCertificates(roles: readonly string[]): boolean {
  return CERTIFICATE_ISSUER_ROLES.some((role) => roles.includes(role));
}

export function reasonProblem(reason: string): string | null {
  const length = reason.trim().length;
  if (length < REASON_MINIMUM) return `Write at least ${REASON_MINIMUM} characters so an auditor can read why.`;
  if (length > REASON_MAXIMUM) return `Keep the reason to ${REASON_MAXIMUM} characters.`;
  return null;
}

export interface ActionState { enabled: boolean; tip: string }

export function printCopyState(roles: readonly string[]): ActionState {
  return mayIssueCertificates(roles) ? { enabled: true, tip: "Print a copy; it is logged in the reprint log" }
    : { enabled: false, tip: `Printing a copy needs ${ROLE_NAMES}` };
}

/** Correction is offered on the current issued version of a release record only (ADR-0023 Q1, Q2). */
export function correctState(certificate: Pick<CertificateDetail, "certificateId" | "documentKind" | "status" | "version">,
  chain: readonly CertificateListRow[] | undefined, roles: readonly string[]): ActionState | null {
  if (certificate.documentKind !== "release_record") return null;
  const latest = chain?.reduce((maximum, row) => Math.max(maximum, row.version), certificate.version) ?? certificate.version;
  if (certificate.status !== "issued" || latest !== certificate.version)
    return { enabled: false, tip: "Only the current version can be corrected" };
  if (!mayIssueCertificates(roles)) return { enabled: false, tip: `Correcting a record needs ${ROLE_NAMES}` };
  return { enabled: true, tip: "Preview what changes, then issue a corrected version" };
}

export function changeLabel(change: CertificateCorrectionChange): string {
  return change.rows > 1 ? `${change.label}, ${change.rows} rows` : change.label;
}

export function factChanges(changes: readonly CertificateCorrectionChange[]): CertificateCorrectionChange[] {
  return changes.filter((change) => !change.derived);
}

/** File name from a Content-Disposition header (RFC 6266 filename* first), or the fallback. */
export function dispositionFileName(header: string | null, fallback: string): string {
  if (!header) return fallback;
  const extended = /filename\*=(?:UTF-8'')?([^;]+)/i.exec(header);
  if (extended?.[1]) { try { return decodeURIComponent(extended[1].trim().replace(/^"|"$/g, "")); } catch { /* fall through */ } }
  const plain = /filename="?([^";]+)"?/i.exec(header);
  return plain?.[1]?.trim() || fallback;
}
