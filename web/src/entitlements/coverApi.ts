import { apiParsed } from "../api/client.ts";
import { coverQuery } from "./coverModel.ts";
import { coverPageSchema, coverSinceSaleSchema, coverSummarySchema, type CoverFilters } from "./coverTypes.ts";
export const coverApi = {
  unit: (id: string, signal?: AbortSignal) => apiParsed(`/api/units/${id}/cover-since-sale`, coverSinceSaleSchema, { signal }),
  page: (filters: CoverFilters, cursor: string | null, signal?: AbortSignal) => apiParsed(`/api/entitlements/cover-changes?${coverQuery(filters, cursor)}&take=25`, coverPageSchema, { signal }),
  summary: (filters: CoverFilters, signal?: AbortSignal) => apiParsed(`/api/entitlements/cover-changes/summary?${coverQuery(filters)}`, coverSummarySchema, { signal }),
};
