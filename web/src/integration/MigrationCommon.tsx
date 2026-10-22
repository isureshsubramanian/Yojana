import { useEffect, useState, type ReactNode } from "react";
import { Link, useLocation } from "react-router";
import { useSession } from "../session/session";
import { Breadcrumbs, PageHeader } from "../shell/PageHeader";
import { mayReadMigration, validationAttentionCount } from "./migrationModel";
import { migrationApi } from "./migrationApi";
import "./migration.css";

const futureTabs = [
  ["Parallel run", "Arrives with P9-T11"],
] as const;

export function MigrationAccess({ children }: { children: ReactNode }) {
  const roles = useSession().roles;
  return mayReadMigration(roles) ? children
    : <main className="content"><div className="note bad" role="alert">Tenant administrator or Migration lead access is required.</div></main>;
}

export function MigrationPage({ title, eyebrow, sub, actions, flaggedClasses, waitingReviews, validationAttention, children }: {
  title: string; eyebrow: ReactNode; sub: ReactNode; actions?: ReactNode; flaggedClasses?: number | undefined; waitingReviews?: number | undefined; validationAttention?: number | undefined; children: ReactNode;
}) {
  return <main className="content" id="main" tabIndex={-1}>
    <Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Integration", to: "/integration" }, { label: "Migration", to: "/integration/migration" }, ...(title === "Migration" ? [] : [{ label: title }])]} />
    <PageHeader eyebrow={eyebrow} title={title} sub={sub} actions={actions} />
    <MigrationTabs flaggedClasses={flaggedClasses} waitingReviews={waitingReviews} validationAttention={validationAttention} />
    <div className="stack migration-stack">{children}</div>
  </main>;
}

export function MigrationTabs({ flaggedClasses, waitingReviews, validationAttention }: { flaggedClasses?: number | undefined; waitingReviews?: number | undefined; validationAttention?: number | undefined }) {
  const pathname = useLocation().pathname;
  const [templateNeedsPerson, setTemplateNeedsPerson] = useState<number>();
  const [latestValidationAttention, setLatestValidationAttention] = useState<number>();
  useEffect(() => {
    const controller = new AbortController();
    Promise.all([
      migrationApi.templateSummary(controller.signal).then(summary => setTemplateNeedsPerson(summary.needsPerson.templates)),
      migrationApi.batches(undefined, controller.signal).then(page => page.items[0] ? migrationApi.validation(page.items[0].batchId, controller.signal) : null)
        .then(run => setLatestValidationAttention(validationAttentionCount(run))),
    ]).catch(() => undefined);
    return () => controller.abort();
  }, []);
  const attention = validationAttention ?? latestValidationAttention;
  const tabs = [["/integration/migration", "Console"], ["/integration/migration/profiling", "Profiling"], ["/integration/migration/review", "Review queue"], ["/integration/migration/templates", "Contract templates"], ["/integration/migration/validation", "Validation and sign-off"], ["/integration/migration/settings", "Settings"]] as const;
  return <nav className="tabs migration-tabs" aria-label="Migration">
    {tabs.map(([to, label]) => <Link key={to} to={to} aria-current={pathname === to ? "page" : undefined}>{label}{label === "Profiling" && flaggedClasses !== undefined && <span className="n">{flaggedClasses.toLocaleString()}</span>}{label === "Review queue" && waitingReviews !== undefined && <span className={`n${waitingReviews > 0 ? " warn" : ""}`}>{waitingReviews.toLocaleString()}</span>}{label === "Contract templates" && templateNeedsPerson !== undefined && <span className={`n${templateNeedsPerson > 0 ? " warn" : ""}`}>{templateNeedsPerson.toLocaleString()}</span>}{label === "Validation and sign-off" && attention !== undefined && <span className={`n${attention > 0 ? " warn" : ""}`}>{attention.toLocaleString()}</span>}</Link>)}
    {futureTabs.map(([label, title]) => <span className="migration-tab-disabled" aria-disabled="true" title={title} key={label}>{label}</span>)}
  </nav>;
}
