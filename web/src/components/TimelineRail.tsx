import { Link } from "react-router";
import type { TimelineEntry } from "./types";

/**
 * A unit's history as the ledger tells it: episodes (ownership, refurbishment, build) as headings, events
 * newest first with their business time; the recorded time is on hover when it differs. Nothing is
 * computed here; the ledger projection returns the entries in order.
 */
export function TimelineRail({ entries, ariaLabel = "Timeline", onSelect, selectedIndex }: { entries: TimelineEntry[]; ariaLabel?: string; onSelect?: (index: number) => void; selectedIndex?: number | null }) {
  return (
    <ol className="timeline" aria-label={ariaLabel}>
      {entries.map((e, i) =>
        e.kind === "episode" ? (
          <li className="episode" key={`ep-${i}`}>{e.label}</li>
        ) : (
          <li className={`ev${e.emphasis ? " " + e.emphasis : ""}${onSelect ? " selectable" : ""}`} key={`ev-${i}`} title={e.recordedAt ? `Recorded ${e.recordedAt}` : undefined}
            aria-selected={onSelect ? selectedIndex === i : undefined} onClick={onSelect ? (ev) => { if (!(ev.target instanceof HTMLElement && ev.target.closest("a"))) onSelect(i); } : undefined}>
            <div className="t">{e.href ? <Link to={e.href}>{e.title}</Link> : e.title}<code>{e.code}</code></div>
            <div className="m" title={e.meta}>{e.meta}</div>
          </li>
        ),
      )}
    </ol>
  );
}
