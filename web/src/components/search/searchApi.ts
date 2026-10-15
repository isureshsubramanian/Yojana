import { api, ApiError } from "../../api/client";
import type { SearchResponse } from "../types";

export type SearchOutcome = { kind: "results"; response: SearchResponse } | { kind: "unavailable" } | { kind: "error"; message: string };

/**
 * Global search over the tenant's index (ADR-0001 "Global search and find-within"; backend P1-T23). Until a
 * module registers the endpoint the host answers 404, which the palette shows as "not in this build".
 */
export async function runSearch(query: string, scope: string | null, signal: AbortSignal): Promise<SearchOutcome> {
  const params = new URLSearchParams({ q: query });
  if (scope) params.set("scope", scope);
  try {
    const response = await api<SearchResponse>(`/api/search?${params.toString()}`, { signal });
    return { kind: "results", response };
  } catch (error) {
    if (error instanceof ApiError && error.status === 404) {
      // Development builds answer from the pattern fixtures so the palette can be exercised; production has no such path.
      if (process.env.NODE_ENV !== "production") {
        const { searchFixture } = await import("../../patterns/fixtures");
        return { kind: "results", response: searchFixture(query) };
      }
      return { kind: "unavailable" };
    }
    if (error instanceof DOMException && error.name === "AbortError") throw error;
    return { kind: "error", message: error instanceof Error ? error.message : String(error) };
  }
}
