import { api } from "../api/client";

export type NumberingMode = "sequence" | "gap_free";

export interface NumberingSeriesSetting {
  series: string;
  label: string;
  mode: NumberingMode;
  example: string;
  gapBehaviour: string;
}

export interface NumberingSettingsView { series: NumberingSeriesSetting[] }

export const numberingApi = {
  settings: (signal?: AbortSignal | undefined) => api<NumberingSettingsView>("/api/numbering/settings", { signal }),
  setMode: (series: string, mode: NumberingMode) => api<{ setting: NumberingSeriesSetting; replayed: boolean }>(`/api/numbering/settings/${encodeURIComponent(series)}`, {
    method: "PUT",
    body: { commandId: crypto.randomUUID(), mode },
  }),
};
