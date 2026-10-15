import type { NavigateFunction } from "react-router";
import { openSearch } from "../components/search/searchStore";

const CHORD_MS = 800;

function inEditable(target: EventTarget | null): boolean {
  if (!(target instanceof HTMLElement)) return false;
  const tag = target.tagName;
  return tag === "INPUT" || tag === "TEXTAREA" || tag === "SELECT" || target.isContentEditable;
}

/**
 * Keyboard-first chrome (MOCKUP-PROCESS criterion 8): ⌘K / Ctrl+K opens search from anywhere, "/" focuses the
 * page's scan field, "g u" goes to units, "n" starts a correction. Single keys are ignored while typing.
 */
export function installShortcuts(navigate: NavigateFunction): () => void {
  let pendingG = 0;
  const onKey = (e: KeyboardEvent) => {
    if ((e.metaKey || e.ctrlKey) && e.key.toLowerCase() === "k") {
      e.preventDefault();
      openSearch();
      return;
    }
    if (e.metaKey || e.ctrlKey || e.altKey || inEditable(e.target)) return;
    const now = Date.now();
    if (e.key === "/") {
      const field = document.querySelector<HTMLElement>("[data-scan-focus]");
      if (field) { e.preventDefault(); field.focus(); }
      return;
    }
    if (e.key === "g") { pendingG = now; return; }
    if (e.key === "u" && now - pendingG < CHORD_MS) { pendingG = 0; navigate("/units"); return; }
    if (e.key === "n") { navigate("/corrections"); }
  };
  window.addEventListener("keydown", onKey);
  return () => window.removeEventListener("keydown", onKey);
}
