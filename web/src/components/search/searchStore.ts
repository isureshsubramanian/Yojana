// Open/close state of the global search palette, shared by the header field, Mod+K, the scan field and the
// /search route. A tiny external store so none of them need a context provider above the router.
import { useSyncExternalStore } from "react";

interface SearchState { open: boolean; query: string }
let state: SearchState = { open: false, query: "" };
const listeners = new Set<() => void>();
const emit = () => listeners.forEach((l) => l());

export function openSearch(query = ""): void { state = { open: true, query }; emit(); }
export function closeSearch(): void { state = { ...state, open: false }; emit(); }

export function useSearchState(): SearchState {
  return useSyncExternalStore((l) => { listeners.add(l); return () => listeners.delete(l); }, () => state, () => state);
}
