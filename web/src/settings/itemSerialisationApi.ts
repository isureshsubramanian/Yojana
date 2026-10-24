import { api } from "../api/client";

export interface ItemSerialisationSettings {
  itemId: string;
  itemCode: string;
  modelNumbers: string[];
}

export const itemSerialisationApi = {
  get: (itemId: string, signal?: AbortSignal) => api<ItemSerialisationSettings>(`/api/settings/items/${itemId}/serialisation`, { signal }),
  save: (itemId: string, modelNumbers: string[]) => api<ItemSerialisationSettings>(`/api/settings/items/${itemId}/serialisation`, { method: "PUT", body: { modelNumbers } }),
};
