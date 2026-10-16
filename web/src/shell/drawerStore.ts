// The drawer (house layout E): the right rail is hidden until a row is clicked; it then shows that row's context
// above the screen's own context; × or Escape closes it. A tiny external store, like the search palette's, so
// pages, the frame and the top bar share it without a provider. Pages register their context with useDrawerContext.
import { useEffect, useSyncExternalStore, type ReactNode } from "react";

export interface DrawerHead { eyebrow: string; title: string; mono?: boolean }
export interface DrawerState { open: boolean; head: DrawerHead | null; selected: ReactNode; context: ReactNode; contextLabel: string }

let state: DrawerState = { open: false, head: null, selected: null, context: null, contextLabel: "Details" };
const listeners = new Set<() => void>();
const emit = () => listeners.forEach((l) => l());

/** Opens the drawer for a clicked row: its head and body sit above the screen's context. */
export function openDrawer(head: DrawerHead, selected: ReactNode): void { state = { ...state, open: true, head, selected }; emit(); }
/** Opens the drawer with the screen's context only (the header's details button). */
export function showDrawerContext(): void { state = { ...state, open: true, head: null, selected: null }; emit(); }
export function toggleDrawer(): void { if (state.open) closeDrawer(); else showDrawerContext(); }
export function closeDrawer(): void { if (!state.open) return; state = { ...state, open: false, head: null, selected: null }; emit(); }
/** Replaces the selected-row part while the drawer stays open (after an action changed the row). */
export function updateDrawer(head: DrawerHead, selected: ReactNode): void { if (state.open) { state = { ...state, head, selected }; emit(); } }
export function setDrawerContext(context: ReactNode, label = "Details"): void { state = { ...state, context, contextLabel: label }; emit(); }
export function drawerState(): DrawerState { return state; }

export function useDrawer(): DrawerState {
  return useSyncExternalStore((l) => { listeners.add(l); return () => listeners.delete(l); }, () => state, () => state);
}

/**
 * A page's screen context (what the fixed rail used to show). Set on render, cleared on unmount; the drawer closes
 * with the page so a stale selection never outlives its screen.
 */
export function useDrawerContext(context: ReactNode, label = "Details"): void {
  useEffect(() => { setDrawerContext(context, label); });
  useEffect(() => () => { state = { open: false, head: null, selected: null, context: null, contextLabel: "Details" }; emit(); }, []);
}
