// Light/dark mode (MOD-02). The theme itself is fixed in index.html (data-theme="glass"); this module owns the
// only JavaScript that touches the mode attribute. An explicit choice is stored per browser; "system" removes it
// so the OS preference applies through CSS. Switching is an attribute swap: nothing re-renders or re-fetches.
import { useSyncExternalStore } from "react";

export type Mode = "system" | "light" | "dark";
const KEY = "yj-mode";
const listeners = new Set<() => void>();

function read(): Mode {
  try {
    const saved = localStorage.getItem(KEY);
    return saved === "light" || saved === "dark" ? saved : "system";
  } catch {
    return "system";
  }
}

export function setMode(mode: Mode): void {
  const html = document.documentElement;
  if (mode === "system") {
    delete html.dataset.mode;
  } else {
    html.dataset.mode = mode;
  }
  try {
    if (mode === "system") localStorage.removeItem(KEY);
    else localStorage.setItem(KEY, mode);
  } catch {
    /* private mode or storage disabled: the attribute still applied for this page */
  }
  listeners.forEach((l) => l());
}

export function useMode(): Mode {
  return useSyncExternalStore(
    (listener) => { listeners.add(listener); return () => listeners.delete(listener); },
    read,
    () => "system",
  );
}

const DARK_QUERY = "(prefers-color-scheme: dark)";

/** The mode actually on screen: the explicit choice, or the OS preference when the choice is "system". */
export function useEffectiveMode(): "light" | "dark" {
  const mode = useMode();
  const systemDark = useSyncExternalStore(
    (listener) => {
      const query = window.matchMedia(DARK_QUERY);
      query.addEventListener("change", listener);
      return () => query.removeEventListener("change", listener);
    },
    () => window.matchMedia(DARK_QUERY).matches,
    () => false,
  );
  return mode === "system" ? (systemDark ? "dark" : "light") : mode;
}
