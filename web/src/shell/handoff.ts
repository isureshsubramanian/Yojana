import { useEffect } from "react";
import { useLocation, useNavigate } from "react-router";

/**
 * Readable values one screen hands the next (a serial list, the identifier that was scanned, the workbench entry).
 * They travel in the router's history state, never the query string: URLs carry opaque ids only, so serials and
 * codes do not land in browser history, proxy logs or shared links.
 */
export interface Handoff { serials?: string; resolvedFrom?: string; entry?: string }

const KEYS = ["serials", "resolvedFrom", "entry"] as const;

export function handoff(state: unknown): Handoff {
  if (typeof state !== "object" || state === null) return {};
  const result: Handoff = {};
  for (const key of KEYS) {
    const value: unknown = Reflect.get(state, key);
    if (typeof value === "string" && value.length > 0) result[key] = value;
  }
  return result;
}

/** The hand-off on the current history entry. */
export function useHandoff(): Handoff {
  return handoff(useLocation().state);
}

/**
 * Moves a readable value from an old bookmarked URL (`?serials=…`) into history state and rewrites the URL without
 * it, so links saved before the change still work but the value leaves the address bar at once.
 */
export function useLegacyParam(param: string, key: keyof Handoff): void {
  const location = useLocation();
  const navigate = useNavigate();
  useEffect(() => {
    const params = new URLSearchParams(location.search);
    const value = params.get(param);
    if (value === null) return;
    params.delete(param);
    const search = params.toString();
    void navigate({ pathname: location.pathname, search: search ? `?${search}` : "", hash: location.hash },
      { replace: true, state: { ...handoff(location.state), [key]: value } });
  }, [location, navigate, param, key]);
}

const OPAQUE_ID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/** A record id from the query string, or "" when the value is not an opaque id (a typed serial or code is ignored). */
export function opaqueParam(params: URLSearchParams, name: string): string {
  const value = params.get(name)?.trim() ?? "";
  return OPAQUE_ID.test(value) ? value : "";
}
