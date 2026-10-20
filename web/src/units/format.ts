// Presentation helpers of the unit screens: pure, unit-tested without a DOM. They format; they never decide.
import type { Tone } from "../components/types";
import type { DocumentRef } from "../api/units";

export function humanise(code: string | null | undefined): string {
  return code ? code.replace(/_/g, " ").replace(/\./g, " ") : "";
}

/** A stored code as a reader's label: "internal_serial" becomes "Internal serial". */
export function sentence(code: string | null | undefined): string {
  const h = humanise(code);
  return h ? h[0]!.toUpperCase() + h.slice(1) : "";
}

export function statusLabel(status: string | null | undefined): string {
  switch (status) {
    case "pre_release": return "Pre-release";
    case "pending_identifier": return "Pending identifier";
    case "scrapped_pre_release": return "Scrapped pre-release";
    default: return sentence(status) || "—";
  }
}

export function statusTone(status: string | null | undefined): Tone {
  switch (status) {
    case "released": return "ok";
    case "pending_identifier": return "warn";
    case "void": case "scrapped": case "scrapped_pre_release": return "bad";
    case "dormant": case "decommissioned": case "merged": case "pre_release": return "neutral";
    default: return "neutral";
  }
}

export function outcomeTone(outcome: string): Tone {
  switch (outcome) {
    case "commissioned": case "change": case "done": case "complete": case "captured": return "ok";
    case "pending": case "skip": case "duplicate": return "warn";
    case "refused": case "block": case "failed": case "missing": case "unexpected": return "bad";
    default: return "neutral";
  }
}

export function fmtDate(iso: string | null | undefined): string {
  if (!iso) return "—";
  return iso.length >= 10 ? iso.slice(0, 10) : iso;
}

export function fmtDateTime(iso: string | null | undefined): string {
  if (!iso) return "—";
  const m = /^(\d{4}-\d{2}-\d{2})T(\d{2}:\d{2})/.exec(iso);
  return m ? `${m[1]} ${m[2]}` : iso;
}

export function shortId(id: string | null | undefined, length = 8): string {
  return id ? id.replace(/-/g, "").slice(0, length).toUpperCase() : "—";
}

/** "type:guid" as the search index writes document links; null when malformed. */
export function parseDocumentParam(value: string | null | undefined): DocumentRef | null {
  if (!value) return null;
  const colon = value.indexOf(":");
  if (colon <= 0) return null;
  const id = value.slice(colon + 1);
  return /^[0-9a-f]{8}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{12}$/i.test(id) ? { type: value.slice(0, colon), id } : null;
}

export function documentLabel(doc: DocumentRef | null | undefined): string {
  if (!doc) return "—";
  return `${humanise(doc.type)} ${shortId(doc.id)}${doc.line != null ? ` line ${doc.line}` : ""}`;
}

/** Where the query text sits inside an identifier, for the <mark> in the results. */
export function matchSpan(value: string, q: string): { start: number; length: number } | null {
  if (!q) return null;
  const i = value.toLowerCase().indexOf(q.toLowerCase());
  return i >= 0 ? { start: i, length: q.length } : null;
}

/** Splits pasted text into serials: newline, comma, semicolon, tab or whitespace separated; blanks dropped; order kept, duplicates removed. */
export function splitSerials(text: string): string[] {
  const seen = new Set<string>();
  const out: string[] = [];
  for (const raw of text.split(/[\s,;]+/)) {
    const s = raw.trim();
    if (!s || seen.has(s.toUpperCase())) continue;
    seen.add(s.toUpperCase());
    out.push(s);
  }
  return out;
}

/** The serial a parsed scan carries: GS1 (21), MH10 (S), or the cleaned string of a plain scan. */
export function serialFromScan(fields: { key: string; value: string }[], cleaned: string): string {
  const hit = fields.find((f) => ["21", "S", "1S", "SER", "serial"].includes(f.key));
  return hit?.value ?? cleaned;
}

export function pct(part: number, whole: number): number {
  return whole <= 0 ? 0 : Math.round((part / whole) * 1000) / 10;
}
