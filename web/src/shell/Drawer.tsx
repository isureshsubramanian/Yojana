import { useEffect, type ReactNode } from "react";
import { Icon } from "./Icons";
import { closeDrawer, useDrawer, useDrawerContext } from "./drawerStore";

/** The drawer itself: rendered once by the frame; pages feed it through the store. */
export function Drawer() {
  const d = useDrawer();
  useEffect(() => {
    if (!d.open) return;
    const onKey = (e: KeyboardEvent) => { if (e.key === "Escape") closeDrawer(); };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [d.open]);
  return (
    <aside className="rail" aria-label={d.head?.title ?? d.contextLabel} aria-hidden={!d.open}>
      <div className="drawer-head">
        <div>
          <div className="eyebrow">{d.head?.eyebrow ?? d.contextLabel}</div>
          {d.head && <div className={`t${d.head.mono ? " serial" : ""}`}>{d.head.title}</div>}
        </div>
        <button type="button" className="icon-btn" onClick={closeDrawer} aria-label="Close details"><Icon name="x" /></button>
      </div>
      {d.selected && <div className="section selected">{d.selected}</div>}
      {d.context}
    </aside>
  );
}

/** Declares a page's screen context (sections of the drawer below the selected row). Renders nothing in place. */
export function Rail({ label = "Details", children }: { label?: string; children: ReactNode }) {
  useDrawerContext(children, label);
  return null;
}
