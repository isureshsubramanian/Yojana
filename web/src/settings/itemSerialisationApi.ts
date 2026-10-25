import * as z from "zod/mini";
import { apiParsed, checkedBody } from "../api/client.ts";

export const itemSerialisationSettings = z.object({ itemId: z.string(), itemCode: z.string(),
  modelNumbers: z.array(z.string()) });
export type ItemSerialisationSettings = z.infer<typeof itemSerialisationSettings>;
const updateItemSerialisationSettings = z.object({ modelNumbers: z.array(z.string()) });
export const erpBomCompletenessSettings = z.object({ checkAgainstErpBom: z.boolean(), version: z.string() });
export type ErpBomCompletenessSettings = z.infer<typeof erpBomCompletenessSettings>;
const updateErpBomCompletenessSettings = z.object({ checkAgainstErpBom: z.boolean(), expectedVersion: z.string() });

export const itemSerialisationApi = {
  get: (itemId: string, signal?: AbortSignal) => apiParsed(`/api/settings/items/${itemId}/serialisation`, itemSerialisationSettings, { signal }),
  save: (itemId: string, modelNumbers: string[]) => apiParsed(`/api/settings/items/${itemId}/serialisation`, itemSerialisationSettings, { method: "PUT", body: checkedBody(updateItemSerialisationSettings, { modelNumbers }) }),
  getBomCompleteness: (signal?: AbortSignal) => apiParsed("/api/settings/as-built-completeness", erpBomCompletenessSettings, { signal }),
  saveBomCompleteness: (settings: ErpBomCompletenessSettings) => apiParsed("/api/settings/as-built-completeness", erpBomCompletenessSettings, {
    method: "PUT", body: checkedBody(updateErpBomCompletenessSettings,
      { checkAgainstErpBom: settings.checkAgainstErpBom, expectedVersion: settings.version }),
  }),
};
