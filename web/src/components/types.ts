// Data shapes the shared components render. Every one of them is produced by the server (MOD-02 contract:
// the client renders results and decides nothing). Modules return these from their endpoints; the
// components never compute an outcome, a relevance or a total themselves.

export type Tone = "ok" | "warn" | "bad" | "info" | "neutral";

/** Every list endpoint returns a page, a total and a continuation; the grid never asks for "all". */
export interface PageResult<TRow> {
  rows: TRow[];
  total: number;
  page: number;
  pageSize: number;
  /** Opaque cursor for the next page when the server pages by key rather than by number; null on the last page. */
  continuation: string | null;
}

export interface Kpi {
  label: string;
  value: string;
  detail?: string | null;
  trend?: "up" | "down" | "flat" | null;
}

export type PreviewOutcome = "change" | "skip" | "block";

/** One unit of a selection-set preview, as the command computed it. */
export interface PreviewRow {
  id: string;
  serial: string;
  current: string | null;
  next: string | null;
  outcome: PreviewOutcome;
  /** The command's own reason: "already at target", "open claim CL-2026-0410", "certificate v2". */
  reason: string | null;
}

export interface PreviewSummary {
  resolved: number;
  change: number;
  skip: number;
  block: number;
}

export interface PreviewResult extends PageResult<PreviewRow> {
  summary: PreviewSummary;
  /** Rows matching the find query, when one is active; equals total otherwise. */
  matches: number;
}

export interface ApprovalItem {
  id: string;
  command: string;
  initiator: string;
  requestedAt: string;
  /** What the selection set addresses: "DN-2026-018432" with sub "Delivery", or a serial pair. */
  selection: string;
  selectionSub: string | null;
  selectionMono: boolean;
  units: number;
  status: string;
  tone: Tone;
  href: string;
}

export type TimelineEntry =
  | { kind: "episode"; label: string }
  | {
      kind: "event";
      title: string;
      /** The event code, for example unit.shipped. */
      code: string;
      /** Business time plus the facts, pre-formatted: "2026-09-01 14:12 · Leeds FG → carrier · delivery DN-2026-018432 line 4". */
      meta: string;
      /** Recorded time, shown on hover when it differs from business time. */
      recordedAt?: string | null;
      emphasis?: "key" | "bad" | null;
      href?: string | null;
    };

export interface FindFacet {
  id: string;
  label: string;
  count: number | null;
}

/** Where a query text matched inside a title; the palette renders these as highlights. */
export interface MatchSpan { start: number; length: number }

export interface MetadataField { label: string; value: string | null; mono?: boolean }

/** The hover metadata card, from the tenant's search metadata profile for the result's entity type. */
export interface ResultMetadata {
  eyebrow: string;
  title: string;
  titleMono: boolean;
  subtitle: string | null;
  fields: MetadataField[];
  actions: { label: string; href: string; primary?: boolean }[];
  note: string | null;
}

export interface SearchResult {
  id: string;
  /** Entity type code, also the icon: unit, document, entitlement, claim, rma, certificate, party. */
  type: string;
  href: string;
  title: string;
  titleMono: boolean;
  matches: MatchSpan[];
  subtitle: string;
  /** 0 to 100 and the explicit reason ("exact match · internal serial"). */
  relevance: number;
  reason: string;
  metadata: ResultMetadata | null;
}

export interface SearchScope { id: string; label: string; count: number }

export interface SearchGroup { label: string; results: SearchResult[] }

export interface SearchResponse {
  query: string;
  tookMs: number;
  scopes: SearchScope[];
  groups: SearchGroup[];
  /** Set when exactly one exact identifier hit exists: the palette opens it directly. */
  openDirectly: string | null;
}
