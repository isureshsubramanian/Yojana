import { useEffect, useMemo, useState } from "react";
import { useParams } from "react-router";
import { DataGrid, KpiStrip, StatusBadge, type GridColumn, type PageResult } from "../components";
import { Breadcrumbs, PageHeader } from "../shell/PageHeader";
import { Icon } from "../shell/Icons";
import { openDrawer } from "../shell/drawerStore";
import { ErrorNote, Kv } from "../units/common";
import { date, RoleAccess, useAction } from "./AdminCommon";
import { ConnectionTabs } from "./ConnectionTabs";
import { extensionApi } from "./extensionApi";
import { extensionFigure, extensionVersionDetail, pollInterval } from "./extensionModel";
import type { ConnectionExtensionView, ExtensionPage } from "./extensionTypes";
import { human } from "./model";
import "./integration.css";
import "./erp-change.css";

export function Component() { return <RoleAccess><ExtensionScreen /></RoleAccess>; }

function ExtensionScreen() {
  const connectionId = useParams().connectionId ?? "";
  const [view, setView] = useState<ConnectionExtensionView | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [revision, setRevision] = useState(0);
  const action = useAction();
  useEffect(() => {
    const controller = new AbortController(); setError(null);
    extensionApi.read(connectionId, controller.signal).then(value => { if (!controller.signal.aborted) setView(value); })
      .catch(reason => { if (!controller.signal.aborted) setError(reason); });
    return () => controller.abort();
  }, [connectionId, revision]);
  const refresh = () => setRevision(value => value + 1);
  const name = view?.connectionName ?? "Connection";
  const actions = view ? <>
    <a className="btn" href={view.installationGuidePath} target="_blank" rel="noreferrer"><Icon name="report" />Installation guide</a>
    <button type="button" className="icon-btn" aria-label="Check the tenant-side extension now" title="Check now"
      disabled={action.busy} onClick={() => void action.run(async () => setView(await extensionApi.check(connectionId)), "Extension check recorded.")}><Icon name="cycle" /></button>
  </> : undefined;
  return <main className="content" id="main" tabIndex={-1}>
    <Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Integration", to: "/integration" }, { label: "Connections", to: "/integration/connections" }, { label: name }, { label: "Extension" }]} />
    <PageHeader eyebrow={view ? `${name} · tenant-side extension` : "Connection"} title="ERP extension"
      sub="The small extension installed in the customer's ERP exposes serials, lots and posted documents to the module. It holds no business rules. The customer's administrator installs it."
      actions={actions} />
    {view && <ConnectionTabs connectionId={connectionId} active="extension" features={view.features} />}
    <div className="stack">
      <ErrorNote error={error} onReload={refresh} />{action.feedback}
      {!view && !error && <p role="status">Loading extension status…</p>}
      {view && <ExtensionBody view={view} />}
    </div>
  </main>;
}

function ExtensionBody({ view }: { view: ConnectionExtensionView }) {
  const columns = useMemo<GridColumn<ExtensionPage>[]>(() => [
    { key: "page", label: "Page", width: 38, render: page => <span className="primary"><span className="t">{page.label}</span><span className="sub">{page.code}</span></span> },
    { key: "status", label: "Status", width: 22, render: page => <StatusBadge tone={page.status === "reachable" ? "ok" : page.status === "not_reachable" ? "bad" : "neutral"}>{page.status === "not_used_yet" ? "Not used yet" : human(page.status)}</StatusBadge> },
    { key: "rows", label: "Rows read today", width: 20, numeric: true, render: page => extensionFigure(page.rowsReadToday) },
    { key: "last", label: "Last change seen", width: 20, render: page => page.status === "not_used_yet" ? page.reason ?? "—" : date(page.lastChangeSeen) },
  ], []);
  const page: PageResult<ExtensionPage> = { rows: view.pages, total: view.pages.length, page: 1, pageSize: Math.max(1, view.pages.length), continuation: null };
  const permissions = view.access.permissionSetWithoutFullAccessRole ? "Least" : "Review";
  return <>
    <KpiStrip label="Extension figures" kpis={[
      { label: "Installed version", value: view.installedVersion, detail: extensionVersionDetail(view.installedVersion, view.shippedVersion) },
      { label: "Data pages reachable", value: `${view.reachablePages} of ${view.usedPages}`, detail: view.lastCheckedAt ? `checked ${date(view.lastCheckedAt)} by ${view.lastCheckedBy ?? "Former or unknown user"}` : "not checked yet" },
      { label: "Permissions", value: permissions, detail: view.access.fullAccessRoleAssigned === null ? "shipped set · remote assignment not recorded" : view.access.fullAccessRoleAssigned ? "full-access role recorded" : "no full-access role" },
      { label: "Change notifications", value: `${view.notifications.liveCount.toLocaleString()} live`, detail: view.notifications.nextRenewalAt ? `next renewal ${date(view.notifications.nextRenewalAt)}` : "no renewal recorded" },
    ]} />
    <div className="erp-change-columns">
      <section className="card" aria-labelledby="extension-pages"><div className="card-header"><h2 id="extension-pages">Data pages <span className="meta">{view.contractVersion} page contract</span></h2></div>
        <DataGrid columns={columns} page={page} rowId={item => item.code} onPage={() => undefined}
          onRowClick={openPage} viewportHeight={528} footer={`${view.pages.length.toLocaleString()} contract pages`} />
      </section>
      <AccessCard view={view} />
    </div>
  </>;
}

function AccessCard({ view }: { view: ConnectionExtensionView }) {
  const access = view.access;
  return <section className="card" aria-labelledby="extension-access"><div className="card-header"><h2 id="extension-access">Access</h2></div><div className="card-body"><ul className="erp-checks">
    <li><span>Application registration for this tenant only<span className="s">one active application credential is kept for this tenant connection</span></span><StatusBadge tone={access.applicationRegistrationPerTenant ? "ok" : "warn"}>{access.applicationRegistrationPerTenant ? "Yes" : "Review"}</StatusBadge></li>
    <li><span>Permission set without a full-access role<span className="s">the shipped set names each object; the remote assignment is not readable</span></span><StatusBadge tone={access.permissionSetWithoutFullAccessRole ? "ok" : "warn"}>{access.permissionSetWithoutFullAccessRole ? "Yes" : "Review"}</StatusBadge></li>
    <li><span>Remote full-access role assignment<span className="s">the page contract does not expose assigned roles</span></span><StatusBadge tone={access.fullAccessRoleAssigned === true ? "bad" : access.fullAccessRoleAssigned === false ? "ok" : "neutral"}>{access.fullAccessRoleAssigned === null ? "Not recorded" : access.fullAccessRoleAssigned ? "Assigned" : "No"}</StatusBadge></li>
    <li><span>Secret expires<span className="s">{access.credentialExpiresAt ? `${date(access.credentialExpiresAt)}${access.credentialReminderAt ? ` · reminder ${date(access.credentialReminderAt)}` : ""}` : access.credentialReplaceBy ? `Not recorded · replace by ${date(access.credentialReplaceBy)}` : "Not recorded"}</span></span><StatusBadge tone={access.credentialExpiresAt ? "ok" : "neutral"}>{access.credentialExpiresAt ? "OK" : "Not recorded"}</StatusBadge></li>
    <li><span>Extension version newer than 1.0<span className="s">the {view.contractVersion} page contract remains fixed</span></span><StatusBadge tone={view.isNewerThanOnePointZero ? "ok" : "neutral"}>{view.isNewerThanOnePointZero ? "Yes" : "No"}</StatusBadge></li>
  </ul></div></section>;
}

function openPage(page: ExtensionPage) {
  openDrawer({ eyebrow: "Data page", title: page.label }, <Kv rows={[
    ["Contract", page.contractVersion],
    ["Status", page.status === "not_used_yet" ? "Not used yet" : human(page.status)],
    ["Reason", page.reason ?? "—"],
    ["Notifications", page.notificationStatus === "live" ? `live${page.notificationExpiresAt ? ` · expires ${date(page.notificationExpiresAt)}` : ""}` : human(page.notificationStatus)],
    ["Fallback poll", pollInterval(page.fallbackPollIntervalSeconds)],
  ]} />);
}
