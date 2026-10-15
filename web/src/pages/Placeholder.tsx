import { useLocation } from "react-router";
import { entryFor } from "../shell/navigation";
import { Breadcrumbs, PageHeader } from "../shell/PageHeader";

/** Stands in for a module whose screens are not part of this build; the route, label and icon are already real. */
export function Component() {
  const { pathname } = useLocation();
  const entry = entryFor(pathname);
  const title = entry?.label ?? "Not available";
  return (
    <main className="content" id="main" tabIndex={-1}>
      <Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: title }]} />
      <PageHeader title={title} sub={entry?.placeholder} />
      <section className="card">
        <div className="empty">
          <h2>Not part of this build</h2>
          <p className="hint">{title} arrives with a later release of the serial and warranty module. The navigation entry is here so the chrome is complete and stable.</p>
        </div>
      </section>
    </main>
  );
}
