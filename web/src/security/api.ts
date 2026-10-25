import * as z from "zod/mini";
import { apiParsed } from "../api/client.ts";
import { session } from "../api/types.ts";
import type { PasswordRuleSet } from "./passwordRulesModel.ts";

export const passwordSettings = z.object({ resetLinkMinutes: z.number(), adminTemporaryPasswordAllowed: z.boolean(),
  invitationValidDays: z.number(), minimumLength: z.number(), requireUppercase: z.boolean(), requireLowercase: z.boolean(),
  requireDigit: z.boolean(), requireSymbol: z.boolean(), version: z.string() });
export type PasswordSettings = z.infer<typeof passwordSettings>;
// The rule hints read this shape; the annotation keeps the schema and passwordRulesModel's type in step.
const passwordRuleSet: z.ZodMiniType<PasswordRuleSet> = z.object({ minimumLength: z.number(), requireUppercase: z.boolean(),
  requireLowercase: z.boolean(), requireDigit: z.boolean(), requireSymbol: z.boolean() });

/** Whose rules to read: a reset link's person, an invitation's company, or (neither) the signed-in person. */
export type PasswordRulesFor = { reset: string } | { invitation: string } | Record<string, never>;

export const passwordApi = {
  request: (email: string) => apiParsed("/api/auth/password-reset/request", z.unknown(), { method: "POST", body: { email } }),
  complete: (token: string, newPassword: string) => apiParsed("/api/auth/password-reset/complete", session, { method: "POST", body: { token, newPassword } }),
  change: (currentPassword: string, newPassword: string) => apiParsed("/api/auth/change-password", z.unknown(), { method: "POST", body: { currentPassword, newPassword } }),
  // POST so the reset or invitation token stays in the body, out of URLs and server logs.
  rules: (whose: PasswordRulesFor, signal?: AbortSignal) => apiParsed("/api/auth/password-rules", passwordRuleSet, { method: "POST", body: whose, ...(signal ? { signal } : {}) }),
  settings: (signal?: AbortSignal) => apiParsed("/api/security/settings/passwords", passwordSettings, signal ? { signal } : undefined),
  saveSettings: (settings: PasswordSettings) => apiParsed("/api/security/settings/passwords", passwordSettings, { method: "PUT", body: settings }),
};
