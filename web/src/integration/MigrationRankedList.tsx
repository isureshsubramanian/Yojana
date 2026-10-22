export interface MigrationRankedCount {
  key: string;
  label: string;
  count: number;
}

export function MigrationRankedList({ headingId, title, meta, rows, total, selected, onSelect }: {
  headingId: string;
  title: string;
  meta: string;
  rows: readonly MigrationRankedCount[];
  total: number;
  selected: string;
  onSelect: (key: string) => void;
}) {
  if (!rows.length) return null;
  const ordered = [...rows].sort((left, right) => right.count - left.count || left.label.localeCompare(right.label));
  return <section className="card" aria-labelledby={headingId}><div className="card-header"><h2 id={headingId}>{title} <span className="meta">{meta}</span></h2></div><ul className="migration-reason-list">
    {ordered.map(row => <li key={row.key}><button type="button" className="migration-reason" aria-pressed={selected === row.key} onClick={() => onSelect(row.key)}><span className="lbl">{row.label}</span><span className="num">{row.count.toLocaleString()}</span><span className="bar" aria-hidden="true"><i style={{ width: `${Math.max(1, Math.round(row.count * 100 / Math.max(1, total)))}%` }} /></span></button></li>)}
  </ul></section>;
}
