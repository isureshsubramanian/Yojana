import { useEffect, useState, type ReactNode } from "react";
import { Link, useLocation } from "react-router";
import { useSession } from "../session/session";
import { integrationApi } from "./api";
import { erpChangeApi } from "./erpChangeApi";
import { erpChangeRights } from "./erpChangeModel";
import { mayOperateIntegration } from "./model";
import { mayReadMigration } from "./migrationModel";

const tabs = [
  ["/integration", "Health"],
  ["/integration/connections", "Connections"],
  ["/integration/messages-attention", "Messages needing attention"],
  ["/integration/manual-tasks", "ERP entries to make by hand"],
  ["/integration/daily-check", "Daily check"],
  ["/integration/history-loads", "History loads"],
  ["/integration/migration", "Migration"],
  ["/integration/erp-change", "ERP change"],
] as const;

export function IntegrationTabs({ attention, manual }: { attention?: number | undefined; manual?: number | undefined }) {
  const { pathname } = useLocation();
  const roles = useSession().roles;
  const operator = mayOperateIntegration(roles);
  const migrationReader = mayReadMigration(roles);
  const erpChangeReader = erpChangeRights(roles).read;
  const changing = useErpChangeInProgress(erpChangeReader);
  return <nav className="tabs integration-subnav" aria-label="Integration">
    {tabs.filter(([to]) => to === "/integration/erp-change" ? erpChangeReader && changing : (operator || to === "/integration" || (to === "/integration/migration" && migrationReader)) && (to !== "/integration/migration" || migrationReader)).map(([to, label]) => {
      const active = pathname === to || (to === "/integration/erp-change" && pathname.startsWith(to));
      const count = label === "Messages needing attention" ? attention : label === "ERP entries to make by hand" ? manual : undefined;
      return <Link key={to} to={to} aria-current={active ? "page" : undefined}>{label}{count !== undefined && <span className={`n${count > 0 ? " warn" : ""}`}>{count.toLocaleString()}</span>}</Link>;
    })}
  </nav>;
}

export function IntegrationAccess({ children }: { children: ReactNode }) {
  const permitted = mayOperateIntegration(useSession().roles);
  return permitted ? children : <div className="note bad" role="alert"><strong>Integration operator access is required.</strong>&nbsp;Service managers can see the top-bar status, while message actions remain restricted.</div>;
}

export function useIntegrationTabCounts(): { attention?: number; manual?: number } {
  const permitted = mayOperateIntegration(useSession().roles);
  const [counts, setCounts] = useState<{ attention?: number; manual?: number }>({});
  useEffect(() => {
    if (!permitted) return;
    const controller = new AbortController();
    Promise.all([
      integrationApi.deadLetters({ status: "needs_attention", take: 1 }, controller.signal),
      integrationApi.manualTasks({ status: "open", take: 1 }, controller.signal),
    ]).then(([messages, tasks]) => setCounts({ attention: messages.summary.open, manual: tasks.desks.reduce((sum, row) => sum + row.count, 0) })).catch(() => undefined);
    return () => controller.abort();
  }, [permitted]);
  return counts;
}

/** The ERP change tab shows only while a change is in progress (owner answer 1, P10 mockups), to everyone who may read it (OD-71). */
function useErpChangeInProgress(permitted: boolean): boolean {
  const [active, setActive] = useState(false);
  useEffect(() => {
    if (!permitted) return;
    const controller = new AbortController();
    erpChangeApi.active(controller.signal).then(() => setActive(true)).catch(() => setActive(false));
    return () => controller.abort();
  }, [permitted]);
  return active;
}
