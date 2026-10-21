import { api } from "../api/client";
import type { Session } from "../api/types";

export interface TenantUserRow {
  userId: string | null; invitationId: string | null; displayName: string; email: string; roles: string[];
  signInMethods: string[]; lastSignInAt: string | null; status: "active" | "invited" | "deactivated" | "locked";
  invitationExpiresAt: string | null; deactivatedAt: string | null; deactivatedBy: string | null;
  lockedUntil: string | null; version: string | null;
  secondStepRequired: boolean; recoveryCodesLeft: number;
}
export interface TenantUserPage { items: TenantUserRow[]; nextCursor: string | null }
export interface TenantUserSummary { active: number; invited: number; deactivated: number; secondStepOn: number; requiredNotSetUp: number }
export interface TenantUserDetail { user: TenantUserRow; alsoMemberOf: string[]; otherMembershipCount: number; passwordChangedAt: string | null; secondStepResetAllowed: boolean }
export interface InvitationInspection { tenantName: string; roles: string[]; email: string; accountExists: boolean }

const query = (values: Record<string, string | number | undefined>) => {
  const p = new URLSearchParams();
  Object.entries(values).forEach(([key, value]) => { if (value !== undefined && value !== "") p.set(key, String(value)); });
  return p.toString();
};

export const userManagementApi = {
  page: (filter: { status?: string; role?: string; q?: string; cursor?: string; take: number }, signal?: AbortSignal) =>
    api<TenantUserPage>(`/api/security/users?${query(filter)}`, { signal }),
  summary: (signal?: AbortSignal) => api<TenantUserSummary>("/api/security/users/summary", { signal }),
  detail: (userId: string, signal?: AbortSignal) => api<TenantUserDetail>(`/api/security/users/${userId}`, { signal }),
  roles: (userId: string, roles: string[], version: string) => api<TenantUserRow>(`/api/security/users/${userId}/roles`, { method: "PUT", body: { roles, version } }),
  deactivate: (userId: string, reason: string) => api<void>(`/api/security/users/${userId}/deactivate`, { method: "POST", body: { reason } }),
  reactivate: (userId: string) => api<void>(`/api/security/users/${userId}/reactivate`, { method: "POST", body: {} }),
  resetSecondStep: (userId: string, reason: string) => api<void>(`/api/security/users/${userId}/second-step-reset`, { method: "POST", body: { reason } }),
  invite: (email: string, displayName: string, roles: string[]) => api<{ invitationId: string; expiresAt: string; mailId: string }>("/api/security/invitations", { method: "POST", body: { email, displayName, roles } }),
  resend: (invitationId: string) => api<{ invitationId: string; expiresAt: string; mailId: string }>(`/api/security/invitations/${invitationId}/resend`, { method: "POST", body: {} }),
  revoke: (invitationId: string) => api<void>(`/api/security/invitations/${invitationId}`, { method: "DELETE" }),
  inspect: (token: string) => api<InvitationInspection>("/api/auth/invitations/inspect", { method: "POST", body: { token } }),
  accept: (token: string, password: string) => api<Session>("/api/auth/invitations/accept", { method: "POST", body: { token, password } }),
  acceptSignedIn: (token: string) => api<Session>("/api/auth/invitations/accept-signed-in", { method: "POST", body: { token } }),
};
