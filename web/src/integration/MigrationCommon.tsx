import { type ReactNode } from "react";
import { Link, useLocation } from "react-router";
import { useSession } from "../session/session";
import { Breadcrumbs, PageHeader } from "../shell/PageHeader";
import "./migration.css";

const futureTabs = [
  ["Review queue", "Arrives with P9-T05"],
  ["Validation and sign-off", "Arrives with P9-T08"],
  ["Parallel run", "Arrives with P9-T11"],
] as const;

export function MigrationAccess({ children }: { children: ReactNode }) {
  return useSession().roles.includes("tenant_administrator") ? children
    : <main className="content"><div className="note bad" role="alert">Tenant administrator access is required.</div></main>;
}

export function MigrationPage({ title, eyebrow, sub, actions, flaggedClasses, children }: {
  title: string; eyebrow: ReactNode; sub: ReactNode; actions?: ReactNode; flaggedClasses?: number | undefined; children: ReactNode;
}) {
  return <main className="content" id="main" tabIndex={-1}>
    <Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Integration", to: "/integration" }, { label: "Migration", to: "/integration/migration" }, ...(title === "Migration" ? [] : [{ label: title }])]} />
    <PageHeader eyebrow={eyebrow} title={title} sub={sub} actions={actions} />
    <MigrationTabs flaggedClasses={flaggedClasses} />
    <div className="stack migration-stack">{children}</div>
  </main>;
}

export function MigrationTabs({ flaggedClasses }: { flaggedClasses?: number | undefined }) {
  const pathname = useLocation().pathname;
  const tabs = [["/integration/migration", "Console"], ["/integration/migration/profiling", "Profiling"]] as const;
  return <nav className="tabs migration-tabs" aria-label="Migration">
    {tabs.map(([to, label]) => <Link key={to} to={to} aria-current={pathname === to ? "page" : undefined}>{label}{label === "Profiling" && flaggedClasses !== undefined && <span className="n">{flaggedClasses.toLocaleString()}</span>}</Link>)}
    {futureTabs.map(([label, title]) => <span className="migration-tab-disabled" aria-disabled="true" title={title} key={label}>{label}</span>)}
  </nav>;
}
