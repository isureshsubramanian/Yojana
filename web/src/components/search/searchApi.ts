import * as z from "zod/mini";
import { apiParsed, ApiError } from "../../api/client";
import type { SearchResponse } from "../types";

// The palette and the pattern fixtures share SearchResponse; the annotation keeps this schema and that type in step.
const metadataField = z.object({ label: z.string(), value: z.nullable(z.string()), mono: z.boolean() });
const resultMetadata = z.object({ eyebrow: z.string(), title: z.string(), titleMono: z.boolean(), subtitle: z.nullable(z.string()),
  fields: z.array(metadataField), actions: z.array(z.object({ label: z.string(), href: z.string(), primary: z.boolean() })), note: z.nullable(z.string()) });
const searchResult = z.object({ id: z.string(), type: z.string(), href: z.string(), title: z.string(), titleMono: z.boolean(),
  matches: z.array(z.object({ start: z.number(), length: z.number() })), subtitle: z.string(), relevance: z.number(), reason: z.string(),
  metadata: z.nullable(resultMetadata) });
const searchResponse: z.ZodMiniType<SearchResponse> = z.object({ query: z.string(), tookMs: z.number(),
  scopes: z.array(z.object({ id: z.string(), label: z.string(), count: z.number() })),
  groups: z.array(z.object({ label: z.string(), results: z.array(searchResult) })), openDirectly: z.nullable(z.string()) });

export type SearchOutcome = { kind: "results"; response: SearchResponse } | { kind: "unavailable" } | { kind: "error"; message: string };

/**
 * Global search over the tenant's index (ADR-0001 "Global search and find-within"; backend P1-T23). Until a
 * module registers the endpoint the host answers 404, which the palette shows as "not in this build".
 */
export async function runSearch(query: string, scope: string | null, signal: AbortSignal): Promise<SearchOutcome> {
  const params = new URLSearchParams({ q: query });
  if (scope) params.set("scope", scope);
  try {
    const response = await apiParsed(`/api/search?${params.toString()}`, searchResponse, { signal });
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
