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

const subscribe = (l: () => void) => { listeners.add(l); return () => { listeners.delete(l); }; };

/** The whole drawer state: for the frame and the drawer itself, which render its content. */
export function useDrawer(): DrawerState {
  return useSyncExternalStore(subscribe, () => state, () => state);
}

/**
 * Whether the drawer is open, for pages. A page must not subscribe through useDrawer: its Rail re-registers the screen
 * context on every render (useDrawerContext), each registration replaces the state, and the page would re-render in a
 * loop. A boolean snapshot is unchanged by those registrations, so React skips the re-render.
 */
export function useDrawerOpen(): boolean {
  return useSyncExternalStore(subscribe, () => state.open, () => state.open);
}

/**
 * A page's screen context (what the fixed rail used to show). Set on render, cleared on unmount; the drawer closes
 * with the page so a stale selection never outlives its screen.
 * Every render re-registers and emits a new state, so the page that calls this (or any ancestor) must read the drawer
 * through useDrawerOpen, never useDrawer: a whole-state subscription above this call re-renders it in a loop.
 */
export function useDrawerContext(context: ReactNode, label = "Details"): void {
  useEffect(() => { setDrawerContext(context, label); });
  useEffect(() => () => { state = { open: false, head: null, selected: null, context: null, contextLabel: "Details" }; emit(); }, []);
}
