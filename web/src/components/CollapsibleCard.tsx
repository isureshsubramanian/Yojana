import { useId, useState, type ReactNode } from "react";
import { Icon } from "../shell/Icons";

export interface CollapsibleCardProps {
  title: string;
  meta?: ReactNode;
  headerActions?: ReactNode;
  storageKey: string;
  children: ReactNode;
}

export function collapsibleCardStartsExpanded(storageKey: string, storage: Pick<Storage, "getItem"> | null): boolean {
  try { return storage?.getItem(storageKey) !== "collapsed"; } catch { return true; }
}

/** A presentation-only card. Its caller owns the content and composes a viewer-specific storage key. */
export function CollapsibleCard({ title, meta, headerActions, storageKey, children }: CollapsibleCardProps) {
  const contentId = `collapsible-${useId().replace(/:/g, "")}`;
  const [expanded, setExpanded] = useState(() => collapsibleCardStartsExpanded(storageKey,
    typeof window === "undefined" ? null : window.localStorage));
  const toggle = () => setExpanded((current) => {
    const next = !current;
    try { window.localStorage.setItem(storageKey, next ? "expanded" : "collapsed"); } catch { /* Storage is optional. */ }
    return next;
  });
  const label = `${expanded ? "Collapse" : "Expand"} ${title}`;
  return <section className="card collapsible-card">
    <div className="card-header">
      <h2>{title}{meta && <span className="meta">{meta}</span>}</h2>
      <div className="collapsible-card-actions">{headerActions}<button type="button" className="icon-btn" title={label}
        aria-label={label} aria-expanded={expanded} aria-controls={contentId} onClick={toggle}>
        <Icon name="chev" className={`ic collapsible-card-chevron${expanded ? " expanded" : ""}`} />
      </button></div>
    </div>
    {expanded && <div id={contentId}>{children}</div>}
  </section>;
}
