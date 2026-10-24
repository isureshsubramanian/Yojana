import { api } from "../api/client";
import type { Session } from "../api/types";
import type { PasswordRuleSet } from "./passwordRulesModel";

export interface PasswordSettings {
  resetLinkMinutes: number;
  adminTemporaryPasswordAllowed: boolean;
  invitationValidDays: number;
  minimumLength: number;
  requireUppercase: boolean;
  requireLowercase: boolean;
  requireDigit: boolean;
  requireSymbol: boolean;
  version: string;
}

/** Whose rules to read: a reset link's person, an invitation's company, or (neither) the signed-in person. */
export type PasswordRulesFor = { reset: string } | { invitation: string } | Record<string, never>;

export const passwordApi = {
  request: (email: string) => api<Record<string, never>>("/api/auth/password-reset/request", { method: "POST", body: { email } }),
  complete: (token: string, newPassword: string) => api<Session>("/api/auth/password-reset/complete", { method: "POST", body: { token, newPassword } }),
  change: (currentPassword: string, newPassword: string) => api<void>("/api/auth/change-password", { method: "POST", body: { currentPassword, newPassword } }),
  // POST so the reset or invitation token stays in the body, out of URLs and server logs.
  rules: (whose: PasswordRulesFor, signal?: AbortSignal) => api<PasswordRuleSet>("/api/auth/password-rules", { method: "POST", body: whose, ...(signal ? { signal } : {}) }),
  settings: (signal?: AbortSignal) => api<PasswordSettings>("/api/security/settings/passwords", signal ? { signal } : undefined),
  saveSettings: (settings: PasswordSettings) => api<PasswordSettings>("/api/security/settings/passwords", { method: "PUT", body: settings }),
};
