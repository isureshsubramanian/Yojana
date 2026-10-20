import { api } from "../api/client.ts";

export interface PasswordResetOptions {
  canSendLink: boolean; canSetTemporary: boolean;
  temporaryUnavailableReason: null | "tenant_setting_off" | "other_memberships" | "no_password_sign_in";
}
export interface PasswordResetResult { mode: "link" | "temporary"; mailId: string | null; temporaryPassword: string | null }

// The Users drawer's client for the administrator reset endpoints (PasswordResetEndpoints, P9-T24).
export const passwordResetApi = {
  options: (userId: string, signal?: AbortSignal) => api<PasswordResetOptions>(`/api/security/users/${userId}/password-reset-options`, { signal }),
  reset: (userId: string, mode: "link" | "temporary") => api<PasswordResetResult>(`/api/security/users/${userId}/password-reset`, { method: "POST", body: { mode } }),
};
