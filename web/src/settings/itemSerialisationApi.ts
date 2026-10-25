import * as z from "zod/mini";
import { apiParsed } from "../api/client";

export const itemSerialisationSettings = z.object({ itemId: z.string(), itemCode: z.string(),
  modelNumbers: z.array(z.string()) });
export type ItemSerialisationSettings = z.infer<typeof itemSerialisationSettings>;

export const itemSerialisationApi = {
  get: (itemId: string, signal?: AbortSignal) => apiParsed(`/api/settings/items/${itemId}/serialisation`, itemSerialisationSettings, { signal }),
  save: (itemId: string, modelNumbers: string[]) => apiParsed(`/api/settings/items/${itemId}/serialisation`, itemSerialisationSettings, { method: "PUT", body: { modelNumbers } }),
};
