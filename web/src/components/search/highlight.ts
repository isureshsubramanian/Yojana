import type { MatchSpan } from "../types";

export type Piece = { text: string; hit: boolean };

/** Splits a title into plain and highlighted pieces from the server's match spans (sorted, non-overlapping). */
export function splitHighlights(title: string, spans: MatchSpan[]): Piece[] {
  const pieces: Piece[] = [];
  let cursor = 0;
  for (const s of [...spans].sort((a, b) => a.start - b.start)) {
    const start = Math.max(cursor, Math.min(s.start, title.length));
    const end = Math.min(title.length, s.start + s.length);
    if (end <= start) continue;
    if (start > cursor) pieces.push({ text: title.slice(cursor, start), hit: false });
    pieces.push({ text: title.slice(start, end), hit: true });
    cursor = end;
  }
  if (cursor < title.length) pieces.push({ text: title.slice(cursor), hit: false });
  return pieces;
}

/** Flat list of results in display order, so arrow keys move across groups. */
export function flatten<T>(groups: { results: T[] }[]): T[] {
  return groups.flatMap((g) => g.results);
}

export function step(index: number, count: number, delta: 1 | -1): number {
  if (count === 0) return -1;
  return (index + delta + count) % count;
}
