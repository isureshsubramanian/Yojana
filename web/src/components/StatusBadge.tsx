import type { Tone } from "./types";

/** Status is text first, colour second (MOCKUP-PROCESS criterion 4): the word carries the meaning, the dot is decoration. */
export function StatusBadge({ tone, children, title }: { tone: Tone; children: React.ReactNode; title?: string }) {
  return <span className={`badge ${tone}`} title={title}>{children}</span>;
}
