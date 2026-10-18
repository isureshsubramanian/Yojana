import type { LabelBindableField, LabelBinding, LabelResolvedBinding, LabelTemplateVersion } from "./types";

export const SUGGESTED_LEGAL_WORDING = "Tampering may affect warranty coverage; see warranty terms";

export type LayoutSegment = { kind: "text" | "placeholder" | "phrase"; text: string };

/** Splits the read-only layout mirror without using HTML injection. Phrase hits win over ordinary text; placeholders stay distinct. */
export function highlightLayout(layout: string, phrases: readonly string[]): LayoutSegment[] {
  const needles = phrases.map((phrase) => phrase.trim()).filter(Boolean).sort((a, b) => b.length - a.length);
  const placeholder = /\{\{[a-z][a-z0-9_]*\}\}/gi;
  const segments: LayoutSegment[] = [];
  let cursor = 0;
  while (cursor < layout.length) {
    placeholder.lastIndex = cursor;
    const nextPlaceholder = placeholder.exec(layout);
    let phraseIndex = -1;
    let phrase = "";
    for (const candidate of needles) {
      const index = phraseBoundaryIndex(layout, candidate, cursor);
      if (index >= 0 && (phraseIndex < 0 || index < phraseIndex || (index === phraseIndex && candidate.length > phrase.length))) {
        phraseIndex = index; phrase = layout.slice(index, index + candidate.length);
      }
    }
    const placeholderIndex = nextPlaceholder?.index ?? -1;
    const nextIndex = [placeholderIndex, phraseIndex].filter((index) => index >= 0).sort((a, b) => a - b)[0] ?? -1;
    if (nextIndex < 0) { segments.push({ kind: "text", text: layout.slice(cursor) }); break; }
    if (nextIndex > cursor) segments.push({ kind: "text", text: layout.slice(cursor, nextIndex) });
    if (phraseIndex === nextIndex) {
      segments.push({ kind: "phrase", text: phrase }); cursor = nextIndex + phrase.length;
    } else if (nextPlaceholder) {
      segments.push({ kind: "placeholder", text: nextPlaceholder[0] }); cursor = nextIndex + nextPlaceholder[0].length;
    }
  }
  return segments.length ? segments : [{ kind: "text", text: "" }];
}

export interface BindingTableRow extends LabelBinding { expiryControlled: boolean; sampleValue: string | null }

export function bindingTableRows(bindings: readonly LabelBinding[], fields: readonly LabelBindableField[], preview: readonly LabelResolvedBinding[]): BindingTableRow[] {
  return bindings.map((binding) => ({
    ...binding,
    expiryControlled: fields.find((field) => field.fieldPath === binding.fieldPath)?.expiryControlled ?? false,
    sampleValue: preview.find((resolved) => resolved.placeholder === binding.placeholder)?.value ?? null,
  }));
}

export function reviewActions(roles: readonly string[], userId: string, version: Pick<LabelTemplateVersion, "status" | "legalReview" | "legalReviewedAt" | "regulated" | "approvedAt" | "actorRef"> | null) {
  const draft = version?.status === "draft";
  const administrator = roles.includes("label_administrator");
  const reviewer = roles.includes("legal_reviewer");
  const author = version?.actorRef === userId;
  return {
    canRequest: Boolean(draft && administrator && version?.legalReview && !version.legalReviewedAt),
    canSign: Boolean(draft && reviewer && version?.legalReview && !version.legalReviewedAt && !author),
    canApprove: Boolean(draft && reviewer && version?.regulated && !version.approvedAt && !author),
    authorConflict: Boolean(draft && reviewer && author && (version?.legalReview || version?.regulated)),
  };
}

export function activationReasons(version: Pick<LabelTemplateVersion, "previewCurrent" | "legalReview" | "legalReviewCurrent" | "regulated" | "approvalCurrent"> | null, dirty: boolean): string[] {
  if (dirty) return ["Save the current draft before activation."];
  if (!version?.previewCurrent) return ["Preview the current saved draft before activation."];
  const reasons: string[] = [];
  if (version.legalReview && !version.legalReviewCurrent) reasons.push("Legal review is required for the current content.");
  if (version.regulated && !version.approvalCurrent) reasons.push("Regulated-label approval is required for the current content.");
  return reasons;
}

export function replaceFlaggedWording(layout: string, phrases: readonly string[]): string {
  let next = layout;
  let replaced = false;
  for (const phrase of [...phrases].sort((a, b) => b.length - a.length)) {
    if (!phrase.trim()) continue;
    const index = phraseBoundaryIndex(next, phrase, 0);
    if (index >= 0) {
      next = next.slice(0, index) + SUGGESTED_LEGAL_WORDING + next.slice(index + phrase.length);
      replaced = true;
    }
  }
  return replaced ? next : `${next.replace(/\s+$/, "")}\n${SUGGESTED_LEGAL_WORDING}`;
}

function phraseBoundaryIndex(text: string, phrase: string, from: number): number {
  const matcher = new RegExp(`(?<![\\p{L}\\p{N}_])${escapeRegExp(phrase.trim())}(?![\\p{L}\\p{N}_])`, "giu");
  matcher.lastIndex = from;
  return matcher.exec(text)?.index ?? -1;
}

function escapeRegExp(value: string): string { return value.replace(/[.*+?^${}()|[\]\\]/g, "\\$&"); }

export function placeholders(layout: string): string[] {
  return [...layout.matchAll(/\{\{([a-z][a-z0-9_]*)\}\}/g)].map((match) => match[1]!).filter((value, index, all) => all.indexOf(value) === index);
}

export function humanise(value: string): string {
  const text = value.replace(/[._]/g, " ");
  return text.charAt(0).toUpperCase() + text.slice(1);
}
