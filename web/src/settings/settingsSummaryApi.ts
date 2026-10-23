import { api } from "../api/client";

export interface SettingsSummaryLine {
  key: string;
  counts: Record<string, number>;
  attention: string | null;
}

export interface SettingsSummaryResponse { lines: SettingsSummaryLine[] }

export const settingsSummaryApi = {
  get: (signal: AbortSignal) => api<SettingsSummaryResponse>("/api/settings/summary", { signal }),
};
