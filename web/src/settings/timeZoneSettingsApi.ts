import { api } from "../api/client";

export interface LegalEntityTimeZoneOverride {
  legalEntityId: string;
  timeZone: string;
}

export interface TimeZoneOption {
  id: string;
  offset: string;
  label: string;
}

export interface LegalEntityTimeZoneOption {
  legalEntityId: string;
  name: string;
  code: string;
}

export interface TenantTimeZoneHistoryRow {
  tenantTimeZone: string | null;
  legalEntityOverrides: LegalEntityTimeZoneOverride[];
  updatedBy: string;
  updatedAt: string;
  validFrom: string;
  validTo: string;
}

export interface TenantTimeZoneSettings {
  tenantTimeZone: string | null;
  legalEntityOverrides: LegalEntityTimeZoneOverride[];
  legalEntities: LegalEntityTimeZoneOption[];
  timeZones: TimeZoneOption[];
  version: string;
  updatedBy: string | null;
  updatedAt: string | null;
  history: TenantTimeZoneHistoryRow[];
}

export const timeZoneSettingsApi = {
  get: (signal?: AbortSignal | undefined) => api<TenantTimeZoneSettings>("/api/settings/time-zone", { signal }),
  save: (settings: TenantTimeZoneSettings) => api<TenantTimeZoneSettings>("/api/settings/time-zone", {
    method: "PUT",
    body: {
      tenantTimeZone: settings.tenantTimeZone,
      legalEntityOverrides: settings.legalEntityOverrides,
      expectedVersion: settings.version,
    },
  }),
};
