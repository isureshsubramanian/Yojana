import { useEffect, useState, type ReactNode } from "react";
import { Link, useLocation } from "react-router";
import { useSession } from "../session/session";
import { integrationApi } from "./api";
import { mayOperateIntegration } from "./model";

const tabs = [
  ["/integration", "Health"],
  ["/integration/connections", "Connections"],
  ["/integration/messages-attention", "Messages needing attention"],
  ["/integration/manual-tasks", "ERP entries to make by hand"],
  ["/integration/daily-check", "Daily check"],
  ["/integration/history-loads", "History loads"],
] as const;

export function IntegrationTabs({ attention, manual }: { attention?: number | undefined; manual?: number | undefined }) {
  const { pathname } = useLocation();
  return <nav className="tabs integration-subnav" aria-label="Integration">
    {tabs.map(([to, label]) => {
      const active = pathname === to;
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
