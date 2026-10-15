import { useEffect } from "react";
import { useSearchParams } from "react-router";
import { openSearch } from "../components/search/searchStore";
import { Breadcrumbs, PageHeader } from "../shell/PageHeader";

/** Deep link for a search: opens the palette with the query over the shell. */
export function Component() {
  const [params] = useSearchParams();
  const q = params.get("q") ?? "";
  useEffect(() => { openSearch(q); }, [q]);
  return (
    <main className="content" id="main" tabIndex={-1}>
      <Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Search" }]} />
      <PageHeader title="Search" sub="Serials, documents, parties and entitlements across the tenant" actions={<button type="button" className="btn primary" onClick={() => openSearch(q)}>Open search<kbd className="key">⌘K</kbd></button>} />
    </main>
  );
}
