import * as z from "zod/mini";
import { apiParsed } from "../api/client";
import { session } from "../api/types";

export const tenantUserRow = z.object({ userId: z.nullable(z.string()), invitationId: z.nullable(z.string()),
  displayName: z.string(), email: z.string(), roles: z.array(z.string()), signInMethods: z.array(z.string()),
  lastSignInAt: z.nullable(z.string()), status: z.enum(["active", "invited", "deactivated", "locked"]),
  invitationExpiresAt: z.nullable(z.string()), deactivatedAt: z.nullable(z.string()),
  deactivatedBy: z.nullable(z.string()), lockedUntil: z.nullable(z.string()), version: z.nullable(z.string()),
  secondStepRequired: z.boolean(), recoveryCodesLeft: z.number(), passkeys: z.number() });
export type TenantUserRow = z.infer<typeof tenantUserRow>;
export const tenantUserPage = z.object({ items: z.array(tenantUserRow), nextCursor: z.nullable(z.string()) });
export type TenantUserPage = z.infer<typeof tenantUserPage>;
export const tenantUserSummary = z.object({ active: z.number(), invited: z.number(), deactivated: z.number(),
  secondStepOn: z.number(), requiredNotSetUp: z.number() });
export type TenantUserSummary = z.infer<typeof tenantUserSummary>;
export const tenantUserDetail = z.object({ user: tenantUserRow, alsoMemberOf: z.array(z.string()),
  otherMembershipCount: z.number(), passwordChangedAt: z.nullable(z.string()), secondStepResetAllowed: z.boolean() });
export type TenantUserDetail = z.infer<typeof tenantUserDetail>;
export const invitationInspection = z.object({ tenantName: z.string(), roles: z.array(z.string()), email: z.string(),
  accountExists: z.boolean() });
export type InvitationInspection = z.infer<typeof invitationInspection>;

const query = (values: Record<string, string | number | undefined>) => {
  const p = new URLSearchParams();
  Object.entries(values).forEach(([key, value]) => { if (value !== undefined && value !== "") p.set(key, String(value)); });
  return p.toString();
};

export const userManagementApi = {
  page: (filter: { status?: string; role?: string; q?: string; cursor?: string; take: number }, signal?: AbortSignal) =>
    apiParsed(`/api/security/users?${query(filter)}`, tenantUserPage, { signal }),
  summary: (signal?: AbortSignal) => apiParsed("/api/security/users/summary", tenantUserSummary, { signal }),
  detail: (userId: string, signal?: AbortSignal) => apiParsed(`/api/security/users/${userId}`, tenantUserDetail, { signal }),
  roles: (userId: string, roles: string[], version: string) => apiParsed(`/api/security/users/${userId}/roles`, tenantUserRow, { method: "PUT", body: { roles, version } }),
  deactivate: (userId: string, reason: string) => apiParsed(`/api/security/users/${userId}/deactivate`, z.unknown(), { method: "POST", body: { reason } }),
  reactivate: (userId: string) => apiParsed(`/api/security/users/${userId}/reactivate`, z.unknown(), { method: "POST", body: {} }),
  resetSecondStep: (userId: string, reason: string) => apiParsed(`/api/security/users/${userId}/second-step-reset`, z.unknown(), { method: "POST", body: { reason } }),
  invite: (email: string, displayName: string, roles: string[]) => apiParsed("/api/security/invitations", z.object({ invitationId: z.string(), expiresAt: z.string(), mailId: z.string() }), { method: "POST", body: { email, displayName, roles } }),
  resend: (invitationId: string) => apiParsed(`/api/security/invitations/${invitationId}/resend`, z.object({ invitationId: z.string(), expiresAt: z.string(), mailId: z.string() }), { method: "POST", body: {} }),
  revoke: (invitationId: string) => apiParsed(`/api/security/invitations/${invitationId}`, z.unknown(), { method: "DELETE" }),
  inspect: (token: string) => apiParsed("/api/auth/invitations/inspect", invitationInspection, { method: "POST", body: { token } }),
  accept: (token: string, password: string) => apiParsed("/api/auth/invitations/accept", session, { method: "POST", body: { token, password } }),
  acceptSignedIn: (token: string) => apiParsed("/api/auth/invitations/accept-signed-in", session, { method: "POST", body: { token } }),
};
