import { Breadcrumbs, PageHeader } from "../shell/PageHeader";
import { useLocation } from "react-router";
import { IntegrationTabs, useIntegrationTabCounts } from "./Common";

export function Component() {
  const counts = useIntegrationTabCounts();
  const path = useLocation().pathname;
  const title = path.endsWith("/connections") ? "Connections" : path.endsWith("/daily-check") ? "Daily check" : path.endsWith("/history-loads") ? "History loads" : "Connection health";
  return <main className="content" id="main" tabIndex={-1}>
    <Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Integration" }, { label: title }]} />
    <PageHeader eyebrow="Integration" title={title} sub="This screen is coming in this phase." />
    <IntegrationTabs {...counts} />
    <section className="card integration-empty" aria-label={`${title} status`}><div className="card-body"><div className="eyebrow">Coming in this phase</div><h2>{title}</h2><p className="muted">The Integration area is ready. This tab will light up as its server-side projection lands.</p></div></section>
  </main>;
}
