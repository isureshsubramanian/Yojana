import { Link } from "react-router";

export interface Crumb { label: string; to?: string }

export function Breadcrumbs({ crumbs }: { crumbs: Crumb[] }) {
  return (
    <ol className="crumbs" aria-label="Breadcrumb">
      {crumbs.map((c, i) => (
        <li key={`${c.label}-${i}`} aria-current={i === crumbs.length - 1 ? "page" : undefined}>
          {c.to && i !== crumbs.length - 1 ? <Link to={c.to}>{c.label}</Link> : c.label}
        </li>
      ))}
    </ol>
  );
}

export function PageHeader({ title, eyebrow, sub, actions }: { title: React.ReactNode; eyebrow?: React.ReactNode | undefined; sub?: React.ReactNode; actions?: React.ReactNode }) {
  return (
    <div className="page-header">
      <div>
        {eyebrow && <div className="eyebrow">{eyebrow}</div>}
        <h1>{title}</h1>
        {sub && <p className="sub">{sub}</p>}
      </div>
      {actions && <div className="actions">{actions}</div>}
    </div>
  );
}
