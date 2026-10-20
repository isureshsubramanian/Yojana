import { api } from "../api/client";
import type { Session } from "../api/types";

export interface PasswordSettings {
  resetLinkMinutes: number;
  adminTemporaryPasswordAllowed: boolean;
  version: string;
}

export const passwordApi = {
  request: (email: string) => api<Record<string, never>>("/api/auth/password-reset/request", { method: "POST", body: { email } }),
  complete: (token: string, newPassword: string) => api<Session>("/api/auth/password-reset/complete", { method: "POST", body: { token, newPassword } }),
  change: (currentPassword: string, newPassword: string) => api<void>("/api/auth/change-password", { method: "POST", body: { currentPassword, newPassword } }),
  settings: (signal?: AbortSignal) => api<PasswordSettings>("/api/security/settings/passwords", signal ? { signal } : undefined),
  saveSettings: (settings: PasswordSettings) => api<PasswordSettings>("/api/security/settings/passwords", { method: "PUT", body: settings }),
};
