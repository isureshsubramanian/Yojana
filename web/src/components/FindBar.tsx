import { useEffect, useRef, useState } from "react";
import { Icon } from "../shell/Icons";
import { ScanInput } from "./ScanInput";
import type { FindFacet } from "./types";

export interface FindBarProps {
  query: string;
  /** Called after a short pause in typing, or at once on a scan; the caller runs the server-side find. */
  onQuery: (query: string) => void;
  facets: FindFacet[];
  activeFacet: string | null;
  onFacet: (id: string | null) => void;
  /** "12 of 1,000 match “2506-4934” · prefix on internal serial, SGTIN and customer serial" */
  status?: React.ReactNode;
  compact?: boolean;
  placeholder?: string;
  ariaLabel: string;
  /** Matches become a selection set for a command. */
  onUseMatches?: (() => void) | undefined;
  matchCount?: number | undefined;
}

const DEBOUNCE_MS = 250;

/**
 * Find within a bounded set (ADR-0001 "Global search and find-within"): a document's serials, a selection-set
 * preview, a list. The find runs server-side over that set only; this bar collects the query (serial, prefix,
 * range, pasted list), the facet chips, and shows the running total. ⌘F focuses it while it is on the page.
 */
export function FindBar({ query, onQuery, facets, activeFacet, onFacet, status, compact, placeholder = "Find a serial, prefix, range or pasted list", ariaLabel, onUseMatches, matchCount }: FindBarProps) {
  const [text, setText] = useState(query);
  const timer = useRef<number | undefined>(undefined);
  const root = useRef<HTMLDivElement>(null);
  useEffect(() => setText(query), [query]);
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if ((e.metaKey || e.ctrlKey) && e.key.toLowerCase() === "f") {
        const input = root.current?.querySelector<HTMLInputElement>("input");
        if (input) { e.preventDefault(); input.focus(); input.select(); }
      }
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, []);
  const change = (next: string) => {
    setText(next);
    window.clearTimeout(timer.current);
    timer.current = window.setTimeout(() => onQuery(next.trim()), DEBOUNCE_MS);
  };
  const clear = () => { window.clearTimeout(timer.current); setText(""); onQuery(""); };

  return (
    <div className={`findbar${compact ? " compact" : ""}`} ref={root} role="search" aria-label={ariaLabel}>
      <ScanInput
        value={text}
        onChange={change}
        onSubmit={(s) => { window.clearTimeout(timer.current); onQuery(s.value); }}
        ariaLabel={ariaLabel}
        placeholder={placeholder}
        hint={<kbd className="key">⌘F</kbd>}
      />
      {facets.map((f) => {
        const on = activeFacet === f.id;
        return (
          <button type="button" key={f.id} className={`chip${on ? " on" : ""}`} aria-pressed={on} onClick={() => onFacet(on ? null : f.id)}>
            {f.label}{f.count !== null && <> · {f.count.toLocaleString()}</>}{on && <Icon name="x" />}
          </button>
        );
      })}
      {text && <button type="button" className="chip on" onClick={clear} aria-label="Clear find">Matches{matchCount !== undefined && <> · {matchCount.toLocaleString()}</>}<Icon name="x" /></button>}
      <span className="grow" />
      {status && <span className="status">{status}</span>}
      {onUseMatches && text && matchCount !== undefined && matchCount > 0 && (
        <button type="button" className="btn sm" onClick={onUseMatches}>Use matches as selection set</button>
      )}
    </div>
  );
}
