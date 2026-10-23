import { Link } from "react-router";
import { Breadcrumbs, PageHeader } from "../shell/PageHeader";

export function Component() {
  return <main className="content" id="main" tabIndex={-1}>
    <Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Settings", to: "/settings" }, { label: "Setting not found" }]} />
    <PageHeader eyebrow="Settings" title="Setting not found" sub="This settings address does not match an available setting." />
    <p><Link className="btn primary" to="/settings">All settings</Link></p>
  </main>;
}
