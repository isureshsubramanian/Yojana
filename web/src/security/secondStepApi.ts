import { api } from "../api/client";
import type { Session, TenantChoice } from "../api/types";

export interface PendingSecondStep {
  secondStepRequired: true;
  methods: Array<"authenticator" | "recovery_code">;
  enrolmentRequired: boolean;
  userName: string;
  tenantSummary: TenantChoice[];
}
export interface EnrolmentStart { enrolmentId: string; otpAuthUri: string; manualKey: string; qrSvg: string }
export interface Confirmation { recoveryCodes: string[]; session: Session | null }
export interface SecondStepState { mode: "required" | "optional" | "off"; enrolled: boolean; required: boolean; addedAt: string | null; recoveryCodesLeft: number }
export interface SecondStepRole { role: string; label: string; users: number; mode: "required" | "optional" | "off" }
export interface SecondStepSettings { roles: SecondStepRole[]; notSetUpCount: number; version: string }

export const secondStepApi = {
  complete: (method: "authenticator" | "recovery_code", code: string) => api<Session>("/api/auth/second-step", { method: "POST", body: { method, code } }),
  start: () => api<EnrolmentStart>("/api/auth/second-step/enrolment/start", { method: "POST", body: {} }),
  confirm: (enrolmentId: string, code: string) => api<Confirmation>("/api/auth/second-step/enrolment/confirm", { method: "POST", body: { enrolmentId, code } }),
  state: (signal?: AbortSignal) => api<SecondStepState>("/api/auth/second-step", signal ? { signal } : undefined),
  recoveryCodes: (code: string) => api<{ recoveryCodes: string[] }>("/api/auth/second-step/recovery-codes", { method: "POST", body: { code } }),
  remove: (currentPassword: string) => api<void>("/api/auth/second-step/remove", { method: "POST", body: { currentPassword } }),
  settings: (signal?: AbortSignal) => api<SecondStepSettings>("/api/security/settings/second-step", signal ? { signal } : undefined),
  saveSettings: (settings: SecondStepSettings) => api<SecondStepSettings>("/api/security/settings/second-step", { method: "PUT", body: { roles: settings.roles.map(({ role, mode }) => ({ role, mode })), version: settings.version } }),
};
