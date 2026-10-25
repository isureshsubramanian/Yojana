import * as z from "zod/mini";
import type { ConnectionFeatures } from "./adminTypes";
import { connectionFeatures } from "./adminTypes.ts";

export const extensionPageStatus = z.enum(["reachable", "not_reachable", "not_used_yet"]);
export type ExtensionPageStatus = z.infer<typeof extensionPageStatus>;
export const extensionAccess = z.object({ applicationRegistrationPerTenant: z.boolean(),
  permissionSetWithoutFullAccessRole: z.boolean(), fullAccessRoleAssigned: z.nullable(z.boolean()),
  credentialExpiresAt: z.nullable(z.string()), credentialReminderAt: z.nullable(z.string()),
  credentialReplaceBy: z.nullable(z.string()) });
export type ExtensionAccess = z.infer<typeof extensionAccess>;
export const extensionNotifications = z.object({ liveCount: z.number(), nextRenewalAt: z.nullable(z.string()) });
export type ExtensionNotifications = z.infer<typeof extensionNotifications>;
export const extensionPage = z.object({ code: z.string(), label: z.string(), contractVersion: z.string(),
  status: extensionPageStatus, reason: z.nullable(z.string()), rowsReadToday: z.nullable(z.number()),
  lastChangeSeen: z.nullable(z.string()), notificationStatus: z.string(),
  notificationExpiresAt: z.nullable(z.string()), fallbackPollIntervalSeconds: z.nullable(z.number()) });
export type ExtensionPage = z.infer<typeof extensionPage>;
export const connectionExtensionView = z.object({ connectionId: z.string(), connectionName: z.string(),
  features: connectionFeatures, installedVersion: z.string(), shippedVersion: z.string(),
  isNewerThanOnePointZero: z.boolean(), contractVersion: z.string(), lastCheckedAt: z.nullable(z.string()),
  lastCheckedBy: z.nullable(z.string()), reachablePages: z.number(), usedPages: z.number(), access: extensionAccess,
  notifications: extensionNotifications, pages: z.array(extensionPage), installationGuidePath: z.string() });
export type ConnectionExtensionView = z.infer<typeof connectionExtensionView>;

