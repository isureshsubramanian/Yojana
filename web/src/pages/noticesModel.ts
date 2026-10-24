/**
 * Splits the generated THIRD-PARTY-NOTICES.txt into its components for the notices page. The file stays the single
 * source: this reads the layout tools/Yojana.Gates writes (a heading between two rules of "=", then "Key: value" lines,
 * a blank line and the licence text). A component the parser cannot read is still shown, as plain text.
 */
export interface NoticeComponent {
  name: string;
  licence: string;
  copyright: string;
  project: string;
  repository: string;
  included: string;
  sources: string[];
  text: string;
}

export interface NoticesDocument { preamble: string; components: NoticeComponent[] }

const RULE = /^={20,}$/;
const FIELDS: Record<string, keyof Pick<NoticeComponent, "licence" | "copyright" | "project" | "repository" | "included">> = {
  "Licence": "licence", "Copyright": "copyright", "Copyright (as declared in the package manifest)": "copyright",
  "Project": "project", "Repository": "repository", "Included because": "included",
};

export function parseNotices(raw: string): NoticesDocument {
  const lines = raw.replace(/\r\n?/g, "\n").split("\n");
  // The CONTENTS list names every component; a ruled heading inside a licence text (LLVM's has several) is not one.
  const contentsAt = lines.findIndex((line) => line.trim() === "CONTENTS");
  const names = new Set<string>();
  for (let index = contentsAt + 1; contentsAt >= 0 && index < lines.length && !RULE.test(lines[index] ?? ""); index++) {
    const entry = /^\s+- (.+)$/.exec(lines[index] ?? "");
    if (entry?.[1]) names.add(entry[1].trim());
  }
  const headings: number[] = [];
  for (let index = 0; index + 2 < lines.length; index++) {
    const heading = (lines[index + 1] ?? "").trim();
    if (RULE.test(lines[index] ?? "") && RULE.test(lines[index + 2] ?? "") && heading && (names.size === 0 || names.has(heading))) headings.push(index);
  }
  const preamble = lines.slice(0, headings[0] ?? lines.length).join("\n").trim();
  const components = headings.map((start, position) => {
    const end = headings[position + 1] ?? lines.length;
    return component((lines[start + 1] ?? "").trim(), lines.slice(start + 3, end));
  });
  return { preamble, components };
}

function component(name: string, body: string[]): NoticeComponent {
  const result: NoticeComponent = { name, licence: "", copyright: "", project: "", repository: "", included: "", sources: [], text: "" };
  let index = 0;
  while (index < body.length && !(body[index] ?? "").trim()) index++;
  for (; index < body.length; index++) {
    const line = body[index] ?? "";
    if (!line.trim()) break;
    const match = /^([^:]{1,60}): (.*)$/.exec(line);
    const key = match ? FIELDS[match[1] ?? ""] : undefined;
    if (match && key) result[key] = (match[2] ?? "").trim();
    else if (/ taken from /.test(line)) result.sources.push(line.trim());
    else break;
  }
  result.text = body.slice(index).join("\n").trim();
  return result;
}

/** Components whose name, licence or copyright contains the query (case-insensitive). */
export function filterNotices(components: readonly NoticeComponent[], query: string): NoticeComponent[] {
  const text = query.trim().toLowerCase();
  if (!text) return [...components];
  return components.filter((row) => [row.name, row.licence, row.copyright].some((value) => value.toLowerCase().includes(text)));
}

/** Licence identifiers with the number of components under each, most common first. */
export function licenceCounts(components: readonly NoticeComponent[]): [string, number][] {
  const counts = new Map<string, number>();
  for (const row of components) { const key = row.licence || "Other"; counts.set(key, (counts.get(key) ?? 0) + 1); }
  return [...counts].sort((a, b) => b[1] - a[1] || a[0].localeCompare(b[0]));
}

/** A licence as a short list label: the generator's "see licence text below (…)" becomes "In text"; the full value shows in the detail. */
export function shortLicence(licence: string): string {
  if (!licence) return "Other";
  if (/^see licence text/i.test(licence)) return "In text";
  return licence.length > 18 ? `${licence.slice(0, 17)}…` : licence;
}
