export type SortDirection = "asc" | "desc";
export interface GridSort { key: string; dir: SortDirection }

export interface GridPreference { sort: GridSort; pageSize: number }

export function gridPreferenceKey(userId: string, gridId: string): string {
  return `yojana:grid:${userId}:${gridId}`;
}

export function readGridPreference(key: string, fallback: GridPreference, storage: Pick<Storage, "getItem"> | null): GridPreference {
  try {
    const raw = storage?.getItem(key);
    if (!raw) return fallback;
    const value = JSON.parse(raw) as Partial<GridPreference>;
    if (!value.sort || typeof value.sort.key !== "string" || (value.sort.dir !== "asc" && value.sort.dir !== "desc")
      || typeof value.pageSize !== "number" || value.pageSize < 1 || value.pageSize > 200) return fallback;
    return { sort: value.sort, pageSize: value.pageSize };
  } catch { return fallback; }
}

export function writeGridPreference(key: string, value: GridPreference, storage: Pick<Storage, "setItem"> | null): void {
  try { storage?.setItem(key, JSON.stringify(value)); } catch { /* Storage is optional. */ }
}

export function pageSizeChoices(defaultSize: number, choices: readonly number[] = [25, 50, 100, 200]): number[] {
  return [...new Set([defaultSize, ...choices])].filter(size => size <= 200).sort((left, right) => left - right);
}

export function nextGridSort(current: GridSort, column: { key: string; defaultDir?: SortDirection }, screenDefault: GridSort): GridSort {
  const first = column.defaultDir ?? "asc";
  if (current.key !== column.key) return { key: column.key, dir: first };
  if (current.dir === first) return { key: column.key, dir: first === "asc" ? "desc" : "asc" };
  return screenDefault;
}
