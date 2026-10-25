import * as z from "zod/mini";
import { apiParsed } from "../api/client.ts";

export const passwordResetOptions = z.object({ canSendLink: z.boolean(), canSetTemporary: z.boolean(),
  temporaryUnavailableReason: z.nullable(z.enum(["tenant_setting_off", "other_memberships", "no_password_sign_in"])) });
export type PasswordResetOptions = z.infer<typeof passwordResetOptions>;
export const passwordResetResult = z.object({ mode: z.enum(["link", "temporary"]), mailId: z.nullable(z.string()),
  temporaryPassword: z.nullable(z.string()) });
export type PasswordResetResult = z.infer<typeof passwordResetResult>;

// The Users drawer's client for the administrator reset endpoints (PasswordResetEndpoints, P9-T24).
export const passwordResetApi = {
  options: (userId: string, signal?: AbortSignal) => apiParsed(`/api/security/users/${userId}/password-reset-options`, passwordResetOptions, { signal }),
  reset: (userId: string, mode: "link" | "temporary") => apiParsed(`/api/security/users/${userId}/password-reset`, passwordResetResult, { method: "POST", body: { mode } }),
};
