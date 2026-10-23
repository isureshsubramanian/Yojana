import type { Tone } from "../components";

export const securityEventKinds = [
  "sign_in_succeeded", "sign_in_failed", "locked_out", "signed_out", "security_stamp_rotated",
  "password_changed", "password_reset_requested", "password_reset_completed", "admin_password_reset",
  "second_step_enrolled", "second_step_removed", "recovery_codes_regenerated", "recovery_code_used",
  "passkey_added", "passkey_removed", "user_invited", "user_deactivated", "user_reactivated",
  "roles_changed", "settings_changed",
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
};

export function securityEventLabel(kind: string): string {
  return labels[kind] ?? kind.replace(/_/g, " ").replace(/^./, character => character.toUpperCase());
}

export function securityEventTone(kind: string): Tone {
  if (kind === "sign_in_succeeded" || kind === "user_reactivated" || kind === "second_step_enrolled" || kind === "passkey_added") return "ok";
  if (kind === "sign_in_failed" || kind === "locked_out" || kind === "user_deactivated") return "bad";
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
