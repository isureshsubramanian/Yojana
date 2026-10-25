import type { GridSort, SortDirection } from "./gridPreferences";
import { nextGridSort } from "./gridPreferences";

export interface SortHeaderProps {
  label: string;
  column: { key: string; defaultDir?: SortDirection };
  sort: GridSort;
  screenDefault: GridSort;
  onSort: (sort: GridSort) => void;
}

function action(label: string, direction: SortDirection): string {
  return `Sort by ${label}, ${direction === "asc" ? "ascending" : "descending"}`;
}

export function SortHeader({ label, column, sort, screenDefault, onSort }: SortHeaderProps) {
  const next = nextGridSort(sort, column, screenDefault);
  const active = sort.key === column.key;
  const arrow = !active ? "↕" : sort.dir === "asc" ? "↑" : "↓";
  return <button type="button" className="sort-header" aria-label={action(label, next.dir)} title={action(label, next.dir)}
    onClick={() => onSort(next)}><span>{label}</span><span className={`sort-arrow${active ? " active" : ""}`} aria-hidden="true">{arrow}</span></button>;
}
