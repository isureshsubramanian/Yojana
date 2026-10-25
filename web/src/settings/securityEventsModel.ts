import type { SecurityEventSummary } from "./securityEventsApi";
import type { Kpi, Tone } from "../components";

export const securityEventKinds = [
  "sign_in_succeeded", "sign_in_failed", "locked_out", "signed_out", "security_stamp_rotated",
  "password_changed", "password_reset_requested", "password_reset_completed", "admin_password_reset",
  "second_step_enrolled", "second_step_removed", "recovery_codes_regenerated", "recovery_code_used",
  "passkey_added", "passkey_removed", "user_invited", "user_deactivated", "user_reactivated",
  "roles_changed", "settings_changed",
  "api_token_issued", "api_token_refused", "api_client_created", "api_client_secret_rotated",
  "api_client_revoked", "api_client_changed",
] as const;

const labels: Record<string, string> = {
  sign_in_succeeded: "Signed in",
  sign_in_failed: "Failed sign-in",
  locked_out: "Locked out",
  signed_out: "Signed out",
  security_stamp_rotated: "Sessions ended",
  password_changed: "Password changed",
  password_reset_requested: "Reset link sent",
  password_reset_completed: "Password reset",
  admin_password_reset: "Administrator password reset",
  second_step_enrolled: "Second step enrolled",
  second_step_removed: "Second step reset",
  recovery_codes_regenerated: "Recovery codes regenerated",
  recovery_code_used: "Recovery code used",
  passkey_added: "Passkey added",
  passkey_removed: "Passkey removed",
  user_invited: "User invited",
  user_deactivated: "User deactivated",
  user_reactivated: "User reactivated",
  roles_changed: "Roles changed",
  settings_changed: "Settings changed",
  api_token_issued: "API token issued",
  api_token_refused: "API token refused",
  api_client_created: "API client created",
  api_client_secret_rotated: "API client secret rotated",
  api_client_revoked: "API client revoked",
  api_client_changed: "API client changed",
};

export function securityEventLabel(kind: string): string {
  return labels[kind] ?? kind.replace(/_/g, " ").replace(/^./, character => character.toUpperCase());
}

// Reason codes the server records with each event (SecurityEventWriter callers in Yojana.Platform/Security), as words.
const reasons: Record<string, string> = {
  password: "Password",
  "password+authenticator": "Password and authenticator app",
  "password+passkey": "Password and passkey",
  "password+recovery_code": "Password and recovery code",
  passkey: "Passkey",
  passwordless: "Passkey only",
  authenticator_added: "Authenticator app added",
  authenticator_replaced: "Authenticator app replaced",
  second_step_code_wrong: "Wrong second-step code",
  credentials_rejected: "Wrong e-mail or password",
  account_lockout: "Too many failed attempts",
  address_limit: "Too many attempts from this address",
  passkey_rejected: "Passkey not accepted",
  passkey_not_allowed: "Passkeys are switched off",
  changed_by_user: "Changed by the person",
  user_requested: "Requested by the person",
};

export function securityEventReason(reason: string | null | undefined): string {
  if (!reason) return "—";
  return reasons[reason] ?? reason.replace(/[_+]/g, " ").replace(/^./, character => character.toUpperCase());
}

export function securityEventTone(kind: string): Tone {
  if (kind === "sign_in_succeeded" || kind === "user_reactivated" || kind === "second_step_enrolled" || kind === "passkey_added") return "ok";
  if (kind === "sign_in_failed" || kind === "locked_out" || kind === "user_deactivated" || kind === "api_token_refused") return "bad";
  if (kind === "second_step_removed" || kind === "passkey_removed" || kind === "security_stamp_rotated") return "warn";
  if (kind === "signed_out") return "neutral";
  return "info";
}

export function browserName(userAgent: string | null): string {
  if (!userAgent) return "Unknown device";
  if (/Edg\//.test(userAgent)) return "Edge";
  if (/Firefox\//.test(userAgent)) return "Firefox";
  if (/Chrome\//.test(userAgent)) return "Chrome";
  if (/Safari\//.test(userAgent)) return "Safari";
  return "Other client";
}

export function shiftDay(day: string, offset: number): string {
  const value = new Date(`${day}T00:00:00Z`);
  value.setUTCDate(value.getUTCDate() + offset);
  return value.toISOString().slice(0, 10);
}

const SUMMARY_CARDS: { label: string; key: keyof SecurityEventSummary }[] = [
  { label: "Sign-ins today", key: "signIns" },
  { label: "Failed sign-ins today", key: "failedSignIns" },
  { label: "Lockouts today", key: "lockouts" },
  { label: "Password resets, 7 days", key: "passwordResets" },
  { label: "Sign-in method changes, 7 days", key: "methodChanges" },
];

export function summaryKpis(summary: SecurityEventSummary | "failed" | null): Kpi[] {
  return SUMMARY_CARDS.map(card => summary === "failed" ? { label: card.label, value: "—", detail: "Couldn't load" }
    : { label: card.label, value: summary === null ? "…" : summary[card.key].toLocaleString() });
}
