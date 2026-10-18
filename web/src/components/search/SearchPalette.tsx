import { useEffect, useRef, useState, type KeyboardEvent } from "react";
import { useNavigate } from "react-router";
import { Icon, type IconName } from "../../shell/Icons";
import { Shortcut } from "../Shortcut";
import type { SearchResponse, SearchResult } from "../types";
import { HoverCard } from "./HoverCard";
import { flatten, splitHighlights, step } from "./highlight";
import { runSearch, type SearchOutcome } from "./searchApi";
import { closeSearch, useSearchState } from "./searchStore";

const DEBOUNCE_MS = 150;
const typeIcon: Record<string, IconName> = { unit: "units", document: "claim", entitlement: "ent", claim: "claim", rma: "rma", certificate: "cert", party: "int" };

/**
 * Mod+K global search: one entry for units and identifiers,
 * documents, entitlements, claims, RMAs, certificates and parties. Results arrive ranked with an explicit
 * reason; the hover card shows the tenant's metadata profile for the selected result. The palette renders
 * what the server returns and ranks nothing itself.
 */
export function SearchPalette() {
  const { open, query: initial } = useSearchState();
  if (!open) return null;
  return <PaletteDialog initialQuery={initial} />;
}

function PaletteDialog({ initialQuery }: { initialQuery: string }) {
  const navigate = useNavigate();
  const [query, setQuery] = useState(initialQuery);
  const [scope, setScope] = useState<string | null>(null);
  const [outcome, setOutcome] = useState<SearchOutcome | null>(null);
  const [busy, setBusy] = useState(false);
  const [index, setIndex] = useState(0);
  const input = useRef<HTMLInputElement>(null);
  const list = useRef<HTMLOListElement>(null);

  useEffect(() => { input.current?.focus(); input.current?.select(); }, []);
  useEffect(() => setQuery(initialQuery), [initialQuery]);

  useEffect(() => {
    const q = query.trim();
    if (!q) { setOutcome(null); return; }
    const controller = new AbortController();
    const timer = window.setTimeout(async () => {
      setBusy(true);
      try {
        const result = await runSearch(q, scope, controller.signal);
        setOutcome(result);
        setIndex(0);
        if (result.kind === "results" && result.response.openDirectly) {
          closeSearch();
          navigate(result.response.openDirectly);
        }
      } catch {
        /* aborted by a newer query */
      } finally {
        setBusy(false);
      }
    }, DEBOUNCE_MS);
    return () => { window.clearTimeout(timer); controller.abort(); };
  }, [query, scope, navigate]);

  const response: SearchResponse | null = outcome?.kind === "results" ? outcome.response : null;
  const results = response ? flatten(response.groups) : [];
  const selected: SearchResult | undefined = results[index];

  useEffect(() => {
    list.current?.querySelector<HTMLElement>(`[data-index="${index}"]`)?.scrollIntoView({ block: "nearest" });
  }, [index]);

  const openResult = (r: SearchResult) => { closeSearch(); navigate(r.href); };
  const onKey = (e: KeyboardEvent) => {
    if (e.key === "Escape") { e.preventDefault(); closeSearch(); return; }
    if (e.key === "ArrowDown") { e.preventDefault(); setIndex((i) => step(i, results.length, 1)); return; }
    if (e.key === "ArrowUp") { e.preventDefault(); setIndex((i) => step(i, results.length, -1)); return; }
    if (e.key === "Enter" && selected) { e.preventDefault(); openResult(selected); return; }
    if (e.key === "Tab" && response && response.scopes.length > 0) {
      e.preventDefault();
      const ids = [null, ...response.scopes.map((s) => s.id)];
      const at = ids.indexOf(scope);
      const next = ids[(at + (e.shiftKey ? -1 : 1) + ids.length) % ids.length] ?? null;
      setScope(next);
    }
  };

  let counter = -1;
  return (
    <div className="overlay" role="presentation" onMouseDown={(e) => { if (e.target === e.currentTarget) closeSearch(); }}>
      <div className="palette" role="dialog" aria-modal="true" aria-label="Global search" onKeyDown={onKey}>
        <div className="left">
          <div className="search">
            <Icon name="search" />
            <input ref={input} type="search" value={query} onChange={(e) => setQuery(e.target.value)} aria-label="Search everything" placeholder="Serial, document, party, RMA…" autoComplete="off" spellCheck={false} />
            <kbd className="key">esc</kbd>
          </div>
          {response && response.scopes.length > 0 && (
            <div className="segmented" role="group" aria-label="Scope">
              <button type="button" aria-pressed={scope === null} onClick={() => setScope(null)}>All<span className="n">{response.scopes.reduce((n, s) => n + s.count, 0)}</span></button>
              {response.scopes.map((s) => (
                <button type="button" key={s.id} aria-pressed={scope === s.id} onClick={() => setScope(scope === s.id ? null : s.id)}>{s.label}<span className="n">{s.count}</span></button>
              ))}
            </div>
          )}
          {!query.trim() && <div className="state">Type a serial, an identifier, a document number or a party. Exact identifier matches open directly.</div>}
          {outcome?.kind === "unavailable" && <div className="state"><strong>Search is not part of this build</strong>The search index arrives with the unit identity release; the palette, scopes and hover card are ready for it.</div>}
          {outcome?.kind === "error" && <div className="state"><strong>Search failed</strong>{outcome.message}</div>}
          {response && results.length === 0 && <div className="state"><strong>No results</strong>Nothing in this tenant matches “{response.query}”.</div>}
          {response && results.length > 0 && (
            <ol className="results" role="listbox" aria-label="Results" ref={list} aria-busy={busy}>
              {response.groups.map((g) => (
                <GroupRows key={g.label} label={g.label} results={g.results} startAt={counter + 1} selectedIndex={index} onHover={setIndex} onOpen={openResult} register={(n) => { counter = n; }} />
              ))}
            </ol>
          )}
          <div className="foot">
            <span className="keys"><kbd className="key">↑</kbd><kbd className="key">↓</kbd></span>move
            <span className="keys"><Shortcut keys="enter" /></span>open
            <span className="keys"><kbd className="key">⇥</kbd></span>scope
            <span className="grow" />
            {response && <>{results.length.toLocaleString()} results in {response.tookMs} ms · ranked by match quality, type weight and recency</>}
          </div>
        </div>
        <HoverCard metadata={selected?.metadata ?? null} />
      </div>
    </div>
  );
}

function GroupRows({ label, results, startAt, selectedIndex, onHover, onOpen, register }: { label: string; results: SearchResult[]; startAt: number; selectedIndex: number; onHover: (i: number) => void; onOpen: (r: SearchResult) => void; register: (last: number) => void }) {
  register(startAt + results.length - 1);
  return (
    <>
      <li className="group">{label}</li>
      {results.map((r, i) => {
        const at = startAt + i;
        return (
          <li key={r.id} className="result" role="option" aria-selected={at === selectedIndex} data-index={at} onMouseEnter={() => onHover(at)} onClick={() => onOpen(r)}>
            <span className="type"><Icon name={typeIcon[r.type] ?? "units"} /></span>
            <span>
              <div className={`t${r.titleMono ? " serial" : ""}`}>{splitHighlights(r.title, r.matches).map((p, j) => p.hit ? <mark key={j}>{p.text}</mark> : <span key={j}>{p.text}</span>)}</div>
              <div className="s">{r.subtitle}</div>
            </span>
            <span className="relevance">
              <span className="bar"><i style={{ width: `${Math.max(0, Math.min(100, r.relevance))}%` }} /></span>
              <span className="pct">{Math.round(r.relevance)}</span>
              <span className="why">{r.reason}</span>
            </span>
          </li>
        );
      })}
    </>
  );
}
