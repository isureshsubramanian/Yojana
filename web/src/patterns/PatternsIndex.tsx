import { Link } from "react-router";
import { ApprovalInbox, openSearch } from "../components";
import { Breadcrumbs, PageHeader } from "../shell/PageHeader";
import { approvals } from "./fixtures";

/** Development-only gallery of the shared components on the vetted pattern pages. Not in production builds. */
export function Component() {
  return (
    <main className="content" id="main" tabIndex={-1}>
      <Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Patterns" }]} />
      <PageHeader title="Pattern pages" sub="Shared components on fixture data, in the chrome every later screen uses. Development builds only." actions={<button type="button" className="btn primary" onClick={() => openSearch("493445")}>Open search with “493445”</button>} />
      <div className="stack">
        <div className="cols-3-2">
          <ApprovalInbox items={approvals} total={7} viewAllHref="/patterns/form" />
          <section className="card">
            <div className="card-header"><h2>Pages</h2></div>
            <div className="card-body" style={{ display: "grid", gap: 8 }}>
              <Link className="btn block" to="/patterns/list">List · server-paged grid with KPI strip and selection</Link>
              <Link className="btn block" to="/patterns/detail">Detail · timeline rail and print-ready record</Link>
              <Link className="btn block" to="/patterns/form">Form · selection-set preview with find-within</Link>
              <Link className="btn block" to="/patterns/document">Document · 1,000 serials with find-within</Link>
            </div>
          </section>
        </div>
      </div>
    </main>
  );
}
