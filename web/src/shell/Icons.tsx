// One inline SVG sprite for the whole product (MOD-02 invariant 2). Symbols are the vetted mockup set.
export type IconName =
  | "units" | "ent" | "corr" | "claim" | "rma" | "cert" | "import" | "int" | "set" | "scan" | "chev" | "bell"
  | "home" | "plus" | "filter" | "print" | "x" | "down" | "search" | "range" | "tree" | "check" | "panel";

export function IconSprite() {
  return (
    <svg width="0" height="0" style={{ position: "absolute" }} aria-hidden="true">
      <symbol id="i-units" viewBox="0 0 24 24"><path d="M3 7l9-4 9 4-9 4-9-4z" /><path d="M3 7v10l9 4 9-4V7" /><path d="M12 11v10" /></symbol>
      <symbol id="i-ent" viewBox="0 0 24 24"><path d="M12 3l8 3v6c0 5-3.5 8-8 9-4.5-1-8-4-8-9V6l8-3z" /><path d="M9 12l2 2 4-4" /></symbol>
      <symbol id="i-corr" viewBox="0 0 24 24"><path d="M4 20h4l10-10-4-4L4 16v4z" /><path d="M12 8l4 4" /></symbol>
      <symbol id="i-claim" viewBox="0 0 24 24"><path d="M6 3h9l5 5v13H6z" /><path d="M15 3v5h5" /><path d="M9 13h6M9 17h6" /></symbol>
      <symbol id="i-rma" viewBox="0 0 24 24"><path d="M4 12a8 8 0 1 0 2.3-5.7" /><path d="M4 4v5h5" /></symbol>
      <symbol id="i-cert" viewBox="0 0 24 24"><circle cx="12" cy="9" r="5" /><path d="M8.5 13.5L7 21l5-2 5 2-1.5-7.5" /></symbol>
      <symbol id="i-import" viewBox="0 0 24 24"><path d="M12 3v12" /><path d="M7 10l5 5 5-5" /><path d="M4 21h16" /></symbol>
      <symbol id="i-int" viewBox="0 0 24 24"><circle cx="5" cy="12" r="2" /><circle cx="19" cy="6" r="2" /><circle cx="19" cy="18" r="2" /><path d="M7 11l10-4M7 13l10 4" /></symbol>
      <symbol id="i-set" viewBox="0 0 24 24"><circle cx="12" cy="12" r="3" /><path d="M12 2v3M12 19v3M2 12h3M19 12h3M4.9 4.9l2.1 2.1M17 17l2.1 2.1M4.9 19.1L7 17M17 7l2.1-2.1" /></symbol>
      <symbol id="i-scan" viewBox="0 0 24 24"><path d="M3 7V4h3M18 4h3v3M21 17v3h-3M6 20H3v-3" /><path d="M7 12h10" /></symbol>
      <symbol id="i-search" viewBox="0 0 24 24"><circle cx="11" cy="11" r="6" /><path d="M20 20l-4.5-4.5" /></symbol>
      <symbol id="i-chev" viewBox="0 0 24 24"><path d="M6 9l6 6 6-6" /></symbol>
      <symbol id="i-bell" viewBox="0 0 24 24"><path d="M6 16V11a6 6 0 0 1 12 0v5l2 2H4l2-2z" /><path d="M10 20a2 2 0 0 0 4 0" /></symbol>
      <symbol id="i-home" viewBox="0 0 24 24"><path d="M3 11l9-7 9 7" /><path d="M5 10v10h14V10" /></symbol>
      <symbol id="i-plus" viewBox="0 0 24 24"><path d="M12 5v14M5 12h14" /></symbol>
      <symbol id="i-filter" viewBox="0 0 24 24"><path d="M4 5h16l-6 8v6l-4-2v-4L4 5z" /></symbol>
      <symbol id="i-print" viewBox="0 0 24 24"><path d="M7 8V3h10v5" /><path d="M5 8h14a2 2 0 0 1 2 2v6h-4v5H7v-5H3v-6a2 2 0 0 1 2-2z" /></symbol>
      <symbol id="i-x" viewBox="0 0 24 24"><path d="M6 6l12 12M18 6L6 18" /></symbol>
      <symbol id="i-down" viewBox="0 0 24 24"><path d="M12 4v16M5 13l7 7 7-7" /></symbol>
      <symbol id="i-range" viewBox="0 0 24 24"><path d="M4 12h16" /><path d="M4 8v8M20 8v8" /><path d="M9 10v4M15 10v4" /></symbol>
      <symbol id="i-tree" viewBox="0 0 24 24"><rect x="3" y="3" width="6" height="5" rx="1" /><rect x="15" y="10" width="6" height="5" rx="1" /><rect x="15" y="17" width="6" height="4" rx="1" /><path d="M6 8v11h9M6 12.5h9" /></symbol>
      <symbol id="i-check" viewBox="0 0 24 24"><path d="M5 12l5 5L20 7" /></symbol>
      <symbol id="i-panel" viewBox="0 0 24 24"><rect x="3" y="4" width="18" height="16" rx="2" /><path d="M15 4v16" /></symbol>
    </svg>
  );
}

export function Icon({ name, className }: { name: IconName; className?: string }) {
  return (
    <svg className={className ?? "ic"} aria-hidden="true">
      <use href={`#i-${name}`} />
    </svg>
  );
}
