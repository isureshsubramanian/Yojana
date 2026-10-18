import { useEffect, useRef } from "react";
import { matchesShortcut, shortcutPresentation } from "./shortcutKeys";

export function Shortcut({ keys }: { keys: string }) {
  const presentation = shortcutPresentation(keys);
  return (
    <span className="shortcut-keys" aria-label={presentation.ariaLabel} title={presentation.ariaLabel}>
      {presentation.keys.map((key, index) => <span key={`${key}-${index}`}>{index > 0 && <span className="shortcut-plus" aria-hidden="true">+</span>}<kbd className="key">{key}</kbd></span>)}
    </span>
  );
}

/** One page-level listener. A modal dialog owns its own primary action and suppresses the page action. */
export function useModEnter(action: () => void | Promise<void>, disabled = false): void {
  const actionRef = useRef(action);
  const disabledRef = useRef(disabled);
  actionRef.current = action;
  disabledRef.current = disabled;

  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      if (!matchesShortcut(event, "mod+enter") || disabledRef.current) return;
      if (document.querySelector('[role="dialog"][aria-modal="true"], dialog[open]')) return;
      event.preventDefault();
      void actionRef.current();
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, []);
}
