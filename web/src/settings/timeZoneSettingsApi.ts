import * as z from "zod/mini";
import { apiParsed } from "../api/client";

export const legalEntityTimeZoneOverride = z.object({ legalEntityId: z.string(), timeZone: z.string() });
export type LegalEntityTimeZoneOverride = z.infer<typeof legalEntityTimeZoneOverride>;
export const timeZoneOption = z.object({ id: z.string(), offset: z.string(), label: z.string() });
export type TimeZoneOption = z.infer<typeof timeZoneOption>;
export const legalEntityTimeZoneOption = z.object({ legalEntityId: z.string(), name: z.string(), code: z.string() });
export type LegalEntityTimeZoneOption = z.infer<typeof legalEntityTimeZoneOption>;
export const tenantTimeZoneHistoryRow = z.object({ tenantTimeZone: z.nullable(z.string()),
  legalEntityOverrides: z.array(legalEntityTimeZoneOverride), updatedBy: z.string(), updatedAt: z.string(),
  validFrom: z.string(), validTo: z.string() });
export type TenantTimeZoneHistoryRow = z.infer<typeof tenantTimeZoneHistoryRow>;
export const tenantTimeZoneSettings = z.object({ tenantTimeZone: z.nullable(z.string()),
  legalEntityOverrides: z.array(legalEntityTimeZoneOverride), legalEntities: z.array(legalEntityTimeZoneOption),
  timeZones: z.array(timeZoneOption), version: z.string(), updatedBy: z.nullable(z.string()),
  updatedAt: z.nullable(z.string()), history: z.array(tenantTimeZoneHistoryRow) });
export type TenantTimeZoneSettings = z.infer<typeof tenantTimeZoneSettings>;

export const timeZoneSettingsApi = {
  get: (signal?: AbortSignal | undefined) => apiParsed("/api/settings/time-zone", tenantTimeZoneSettings, { signal }),
  save: (settings: TenantTimeZoneSettings) => apiParsed("/api/settings/time-zone", tenantTimeZoneSettings, {
    method: "PUT",
    body: {
      tenantTimeZone: settings.tenantTimeZone,
      legalEntityOverrides: settings.legalEntityOverrides,
      expectedVersion: settings.version,
    },
  }),
};
