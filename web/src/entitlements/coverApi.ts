import { api } from "../api/client";
import { coverQuery } from "./coverModel";
import type { CoverFilters, CoverPage, CoverSinceSale, CoverSummary } from "./coverTypes";
export const coverApi = {
  unit: (id: string, signal?: AbortSignal) => api<CoverSinceSale>(`/api/units/${id}/cover-since-sale`, { signal }),
  page: (filters: CoverFilters, cursor: string | null, signal?: AbortSignal) => api<CoverPage>(`/api/entitlements/cover-changes?${coverQuery(filters, cursor)}&take=25`, { signal }),
  summary: (filters: CoverFilters, signal?: AbortSignal) => api<CoverSummary>(`/api/entitlements/cover-changes/summary?${coverQuery(filters)}`, { signal }),
};
