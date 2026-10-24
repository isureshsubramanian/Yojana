import { useEffect, useMemo, useState, type KeyboardEvent } from "react";
import { Link } from "react-router";
import { Icon } from "../shell/Icons";
import { Breadcrumbs } from "../shell/PageHeader";
import { filterNotices, licenceCounts, parseNotices, shortLicence, type NoticesDocument } from "./noticesModel";
import "./notices.css";

// The page reads the shipped THIRD-PARTY-NOTICES.txt from /notices (public, as the licences require) and lays it out;
// the file stays the single source and "Download as text" serves it unchanged. Signed in, it opens inside the app frame
// (this route); signed out, PublicNotices shows the same view in the bare frame.
export function Component() {
  return <main className="content" id="main" tabIndex={-1}>
    <Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Third-party notices" }]} />
    <NoticesView />
  </main>;
}

export function NoticesView({ back }: { back?: { label: string; to: string } }) {
  const [doc, setDoc] = useState<NoticesDocument | null>(null);
  const [failed, setFailed] = useState(false);
  const [query, setQuery] = useState("");
  const [selected, setSelected] = useState<string | null>(null);

  useEffect(() => {
    const controller = new AbortController();
    fetch("/notices", { signal: controller.signal })
      .then((response) => { if (!response.ok) throw new Error(String(response.status)); return response.text(); })
      .then((text) => { const parsed = parseNotices(text); setDoc(parsed); setSelected(parsed.components[0]?.name ?? null); })
      .catch((reason: unknown) => { if (!(reason instanceof DOMException && reason.name === "AbortError")) setFailed(true); });
    return () => controller.abort();
  }, []);

  const shown = useMemo(() => filterNotices(doc?.components ?? [], query), [doc, query]);
  const counts = useMemo(() => licenceCounts(doc?.components ?? []), [doc]);
  const current = doc?.components.find((row) => row.name === selected) ?? shown[0] ?? null;

  const onListKey = (event: KeyboardEvent<HTMLUListElement>) => {
    if (event.key !== "ArrowDown" && event.key !== "ArrowUp") return;
    event.preventDefault();
    const at = shown.findIndex((row) => row.name === current?.name);
    const next = shown[Math.min(shown.length - 1, Math.max(0, at + (event.key === "ArrowDown" ? 1 : -1)))];
    if (next) { setSelected(next.name); document.getElementById(optionId(next.name))?.focus(); }
  };

  return <div className="notices-page">
    <div className="page-header"><div><div className="eyebrow">Yojana · legal</div><h1>Third-party notices</h1>
      <p className="sub">Yojana includes the components below. Each is listed with its licence and the copyright its authors declared. Listing a licensor is a licence requirement, not an endorsement.</p></div>
      <div className="actions">{back && <Link className="btn ghost" to={back.to}>{back.label}</Link>}<a className="btn" href="/notices?download" download="THIRD-PARTY-NOTICES.txt"><Icon name="down" />Download as text</a></div></div>
    {failed && <div className="note warn" role="alert"><strong>The notices could not be loaded.</strong>&nbsp;<a href="/notices?download">Download the text file</a> instead.</div>}
    {!doc && !failed && <div className="note" role="status">Loading notices…</div>}
    {doc && <>
      <div className="toolbar notices-toolbar">
        <input className="input notices-find" type="search" placeholder="Find a component, licence or copyright holder" aria-label="Find a component" value={query} onChange={(event) => setQuery(event.target.value)} />
        <span className="muted">{doc.components.length} components · {counts.slice(0, 4).map(([licence, n]) => `${shortLicence(licence)} ${n}`).join(" · ")}{counts.length > 4 ? ` · ${counts.length - 4} more licences` : ""}</span>
      </div>
      <div className="notices-layout">
        <section className="card notices-list" aria-labelledby="notices-components"><div className="card-header"><h2 id="notices-components">Components <span className="meta">{shown.length === doc.components.length ? shown.length : `${shown.length} of ${doc.components.length}`}</span></h2></div>
          {shown.length === 0 ? <p className="muted notices-empty">No component matches “{query.trim()}”.</p>
            : <ul role="listbox" aria-label="Components" onKeyDown={onListKey}>{shown.map((row) => <li key={row.name} id={optionId(row.name)} role="option" tabIndex={row.name === current?.name ? 0 : -1} aria-selected={row.name === current?.name} onClick={() => setSelected(row.name)} onKeyDown={(event) => { if (event.key === "Enter" || event.key === " ") { event.preventDefault(); setSelected(row.name); } }}>
              <span className="notices-name">{row.name}</span><span className="notices-licence" title={row.licence}>{shortLicence(row.licence)}</span></li>)}</ul>}
        </section>
        {current && <section className="card notices-detail" aria-labelledby="notices-current"><div className="card-header"><h2 id="notices-current">{current.name}</h2><span className="meta">{current.licence}</span></div>
          <div className="card-body">
            <dl className="notices-facts">
              <dt>Licence</dt><dd>{current.licence || "—"}</dd>
              <dt>Copyright</dt><dd>{current.copyright || "—"}</dd>
              {(current.project || current.repository) && <><dt>Project</dt><dd><a href={current.project || current.repository} target="_blank" rel="noopener noreferrer">{(current.project || current.repository).replace(/^https?:\/\//, "")}</a></dd></>}
              {current.included && <><dt>Included because</dt><dd>{current.included}</dd></>}
              {current.sources.map((line) => <dd className="notices-source" key={line}>{line}</dd>)}
            </dl>
            <pre className="notices-text">{current.text}</pre>
          </div></section>}
      </div>
    </>}
  </div>;
}

function optionId(name: string): string { return `notice-${name.replace(/[^A-Za-z0-9]+/g, "-")}`; }
