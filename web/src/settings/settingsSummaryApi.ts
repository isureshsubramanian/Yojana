import * as z from "zod/mini";
import { apiParsed } from "../api/client";

export const settingsSummaryLine = z.object({ key: z.string(), counts: z.record(z.string(), z.number()),
  attention: z.nullable(z.string()) });
export type SettingsSummaryLine = z.infer<typeof settingsSummaryLine>;
export const settingsSummaryResponse = z.object({ lines: z.array(settingsSummaryLine) });
export type SettingsSummaryResponse = z.infer<typeof settingsSummaryResponse>;

export const settingsSummaryApi = {
  get: (signal: AbortSignal) => apiParsed("/api/settings/summary", settingsSummaryResponse, { signal }),
};
