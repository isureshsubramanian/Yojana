import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import type * as z from "zod/mini";
import { apiParsed } from "../api/client.ts";
import { gridPreferenceKey, readGridPreference, writeGridPreference, type GridSort } from "./gridPreferences.ts";

export interface GridPageFacts { continuation: string | null }

export interface GridQueryOptions<TData> {
  gridId: string;
  userId: string;
  defaultSort: GridSort;
  defaultSize: number;
  params?: URLSearchParams;
  paging?: "cursor" | "offset";
  pageFacts?: (data: TData) => GridPageFacts;
  enabled?: boolean;
  reloadKey?: unknown;
  sortKeys?: readonly string[];
}

export interface GridQueryState { sort: GridSort; pageSize: number; page: number; cursor: string | null }

export function resetGridQuery(state: GridQueryState, patch: Partial<Pick<GridQueryState, "sort" | "pageSize">>): GridQueryState {
  return { ...state, ...patch, page: 1, cursor: null };
}

/** Shared grid state for POST-backed previews that cannot be fetched by the GET-only query hook. */
export function useGridState(gridId: string, userId: string, defaultSort: GridSort, defaultSize: number) {
  const storageKey = gridPreferenceKey(userId, gridId);
  const [state, setState] = useState<GridQueryState>(() => ({ ...readGridPreference(storageKey, { sort: defaultSort, pageSize: defaultSize }, typeof window === "undefined" ? null : window.localStorage), page: 1, cursor: null }));
  const change = (patch: Partial<Pick<GridQueryState, "sort" | "pageSize">>) => setState(current => {
    const next = resetGridQuery(current, patch);
    writeGridPreference(storageKey, { sort: next.sort, pageSize: next.pageSize }, typeof window === "undefined" ? null : window.localStorage);
    return next;
  });
  return { ...state, onPage: (page: number) => setState(current => ({ ...current, page: Math.max(1, page), cursor: null })), onSort: (sort: GridSort) => change({ sort }), onPageSize: (pageSize: number) => change({ pageSize }), reset: () => setState(current => ({ ...current, page: 1, cursor: null })) };
}

/** One data-grid query seam: state, opaque continuation history, query construction and checked response parsing. */
export function useGridQuery<TSchema extends z.ZodMiniType>(endpoint: string, schema: TSchema, options: GridQueryOptions<z.infer<TSchema>>) {
  type TData = z.infer<TSchema>;
  const storageKey = gridPreferenceKey(options.userId, options.gridId);
  const fallback = { sort: options.defaultSort, pageSize: options.defaultSize };
  const remembered = useMemo(() => readGridPreference(storageKey, fallback,
    typeof window === "undefined" ? null : window.localStorage), [storageKey]);
  const initial = options.sortKeys && !options.sortKeys.includes(remembered.sort.key) ? fallback : remembered;
  const [state, setState] = useState<GridQueryState>({ ...initial, page: 1, cursor: null });
  const [cursors, setCursors] = useState<Map<number, string | null>>(() => new Map([[1, null]]));
  const [data, setData] = useState<TData | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const filterText = options.params?.toString() ?? "";
  const previousFilter = useRef(filterText);
  const paging = options.paging ?? "offset";
  const queryString = useMemo(() => {
    const query = new URLSearchParams(filterText);
    query.set("sort", state.sort.key); query.set("dir", state.sort.dir); query.set("take", String(state.pageSize));
    if (paging === "offset") query.set("skip", String((state.page - 1) * state.pageSize));
    else if (state.cursor) query.set("cursor", state.cursor);
    return query.toString();
  }, [filterText, paging, state]);

  useEffect(() => {
    if (previousFilter.current !== filterText) {
      previousFilter.current = filterText;
      setState(current => ({ ...current, page: 1, cursor: null }));
      setCursors(new Map([[1, null]]));
    }
  }, [filterText]);

  useEffect(() => {
    if (options.enabled === false) { setData(null); setLoading(false); setError(null); return; }
    const controller = new AbortController(); setLoading(true); setError(null);
    void apiParsed(`${endpoint}${queryString ? `?${queryString}` : ""}`, schema, { signal: controller.signal })
      .then(value => { setData(value); setLoading(false); })
      .catch(reason => { if (!(reason instanceof DOMException && reason.name === "AbortError")) { setError(reason); setLoading(false); } });
    return () => controller.abort();
  }, [endpoint, options.enabled, options.reloadKey, queryString, schema]);

  const change = (patch: Partial<Pick<GridQueryState, "sort" | "pageSize">>) => {
    setState(current => {
      const next = resetGridQuery(current, patch);
      writeGridPreference(storageKey, { sort: next.sort, pageSize: next.pageSize },
        typeof window === "undefined" ? null : window.localStorage);
      return next;
    });
    setCursors(new Map([[1, null]]));
  };
  const onPage = (page: number) => {
    if (page < 1) return;
    if (paging === "cursor" && page > state.page && data && options.pageFacts) {
      const next = options.pageFacts(data).continuation;
      if (!next) return;
      setCursors(current => new Map(current).set(page, next));
      setState(current => ({ ...current, page, cursor: next }));
      return;
    }
    setState(current => ({ ...current, page, cursor: cursors.get(page) ?? null }));
  };
  const reload = useCallback(() => setState(current => ({ ...current })), []);
  return { data, loading, error, queryString, ...state, onPage, onSort: (sort: GridSort) => change({ sort }), onPageSize: (pageSize: number) => change({ pageSize }), reload };
}
