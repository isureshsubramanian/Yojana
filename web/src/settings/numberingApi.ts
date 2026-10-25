import * as z from "zod/mini";
import { apiParsed } from "../api/client";

export const numberingMode = z.enum(["sequence", "gap_free"]);
export type NumberingMode = z.infer<typeof numberingMode>;
export const numberingSeriesSetting = z.object({ series: z.string(), label: z.string(), mode: numberingMode,
  example: z.string(), gapBehaviour: z.string() });
export type NumberingSeriesSetting = z.infer<typeof numberingSeriesSetting>;
export const numberingSettingsView = z.object({ series: z.array(numberingSeriesSetting) });
export type NumberingSettingsView = z.infer<typeof numberingSettingsView>;

export const numberingApi = {
  settings: (signal?: AbortSignal | undefined) => apiParsed("/api/numbering/settings", numberingSettingsView, { signal }),
  setMode: (series: string, mode: NumberingMode) => apiParsed(`/api/numbering/settings/${encodeURIComponent(series)}`, z.object({ setting: numberingSeriesSetting, replayed: z.boolean() }), {
    method: "PUT",
    body: { commandId: crypto.randomUUID(), mode },
  }),
};
