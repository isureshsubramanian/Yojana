import type { GridSort } from "./gridPreferences";

/** The part of a grid column the caption needs; GridColumn satisfies it. */
export interface CaptionColumn { label: string; sort?: { key: string } | undefined }

/**
 * The "Sorted by …" caption above or under a grid. While the screen's own default order applies it keeps the screen's
 * wording (for example "Sorted by age, oldest first"); after the user re-sorts it names the active column and
 * direction, so the caption never contradicts the header arrow.
 */
export function sortCaption(columns: readonly CaptionColumn[], grid: { sort: GridSort; defaultSort: GridSort }, defaultCaption: string): string {
  if (grid.sort.key === grid.defaultSort.key && grid.sort.dir === grid.defaultSort.dir) return defaultCaption;
  const column = columns.find((candidate) => candidate.sort?.key === grid.sort.key);
  if (!column) return defaultCaption;
  return `Sorted by ${column.label.toLocaleLowerCase()}, ${grid.sort.dir === "asc" ? "ascending" : "descending"}`;
}
