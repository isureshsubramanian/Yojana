import type { ConnectionFeatures } from "./adminTypes";

export type ExtensionPageStatus = "reachable" | "not_reachable" | "not_used_yet";

export interface ExtensionAccess {
  applicationRegistrationPerTenant: boolean;
  permissionSetWithoutFullAccessRole: boolean;
  fullAccessRoleAssigned: boolean | null;
  credentialExpiresAt: string | null;
  credentialReminderAt: string | null;
  credentialReplaceBy: string | null;
}

export interface ExtensionNotifications { liveCount: number; nextRenewalAt: string | null }

export interface ExtensionPage {
  code: string;
  label: string;
  contractVersion: string;
  status: ExtensionPageStatus;
  reason: string | null;
  rowsReadToday: number | null;
  lastChangeSeen: string | null;
  notificationStatus: string;
  notificationExpiresAt: string | null;
  fallbackPollIntervalSeconds: number | null;
}

export interface ConnectionExtensionView {
  connectionId: string;
  connectionName: string;
  features: ConnectionFeatures;
  installedVersion: string;
  shippedVersion: string;
  isNewerThanOnePointZero: boolean;
  contractVersion: string;
  lastCheckedAt: string | null;
  lastCheckedBy: string | null;
  reachablePages: number;
  usedPages: number;
  access: ExtensionAccess;
  notifications: ExtensionNotifications;
  pages: ExtensionPage[];
  installationGuidePath: string;
}
