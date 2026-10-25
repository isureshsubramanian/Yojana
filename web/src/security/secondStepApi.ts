import * as z from "zod/mini";
import { apiParsed } from "../api/client.ts";
import { session, tenantChoice } from "../api/types.ts";

const secondStepMode = z.enum(["required", "optional", "off"]);
export const pendingSecondStep = z.object({ secondStepRequired: z.literal(true),
  methods: z.array(z.enum(["authenticator", "recovery_code", "passkey"])), enrolmentRequired: z.boolean(), userName: z.string(),
  tenantSummary: z.array(tenantChoice) });
export type PendingSecondStep = z.infer<typeof pendingSecondStep>;
export const enrolmentStart = z.object({ enrolmentId: z.string(), otpAuthUri: z.string(), manualKey: z.string(), qrSvg: z.string() });
export type EnrolmentStart = z.infer<typeof enrolmentStart>;
export const confirmation = z.object({ recoveryCodes: z.array(z.string()), session: z.nullable(session) });
export type Confirmation = z.infer<typeof confirmation>;
export const secondStepState = z.object({ mode: secondStepMode, enrolled: z.boolean(), required: z.boolean(),
  addedAt: z.nullable(z.string()), recoveryCodesLeft: z.number(), passkeys: z.number() });
export type SecondStepState = z.infer<typeof secondStepState>;
export const secondStepRole = z.object({ role: z.string(), label: z.string(), users: z.number(), mode: secondStepMode });
export type SecondStepRole = z.infer<typeof secondStepRole>;
export const secondStepSettings = z.object({ roles: z.array(secondStepRole), notSetUpCount: z.number(), version: z.string() });
export type SecondStepSettings = z.infer<typeof secondStepSettings>;

export const secondStepApi = {
  complete: (method: "authenticator" | "recovery_code", code: string) => apiParsed("/api/auth/second-step", session, { method: "POST", body: { method, code } }),
  start: () => apiParsed("/api/auth/second-step/enrolment/start", enrolmentStart, { method: "POST", body: {} }),
  /** Replacing a working authenticator from a signed-in session also needs the current password. */
  confirm: (enrolmentId: string, code: string, currentPassword?: string) => apiParsed("/api/auth/second-step/enrolment/confirm", confirmation, { method: "POST", body: { enrolmentId, code, currentPassword: currentPassword ?? null } }),
  state: (signal?: AbortSignal) => apiParsed("/api/auth/second-step", secondStepState, signal ? { signal } : undefined),
  recoveryCodes: (code: string) => apiParsed("/api/auth/second-step/recovery-codes", z.object({ recoveryCodes: z.array(z.string()) }), { method: "POST", body: { code } }),
  remove: (currentPassword: string) => apiParsed("/api/auth/second-step/remove", z.unknown(), { method: "POST", body: { currentPassword } }),
  settings: (signal?: AbortSignal) => apiParsed("/api/security/settings/second-step", secondStepSettings, signal ? { signal } : undefined),
  saveSettings: (settings: SecondStepSettings) => apiParsed("/api/security/settings/second-step", secondStepSettings, { method: "PUT", body: { roles: settings.roles.map(({ role, mode }) => ({ role, mode })), version: settings.version } }),
};
