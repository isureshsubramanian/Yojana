import { useEffect, useMemo, useState } from "react";
import { Link } from "react-router";
import { useSession } from "../session/session";
import { Icon } from "../shell/Icons";
import { Breadcrumbs } from "../shell/PageHeader";
import { visibleSettings, type SettingsGroup } from "./registry";
import { filterSettings, loadCurrentValues } from "./registryModel";

const groups: SettingsGroup[] = ["Organisation", "Documents", "Service operations", "Data and knowledge"];

export function Component() {
  const session = useSession();
  const entries = useMemo(() => visibleSettings(session.roles), [session.roles]);
  const [query, setQuery] = useState("");
  const [values, setValues] = useState<Record<string, string>>({});
  const shown = filterSettings(entries, query);

  useEffect(() => {
    const controller = new AbortController();
    void loadCurrentValues(entries, controller.signal).then(setValues).catch(error => { if (!controller.signal.aborted) console.error(error); });
    return () => controller.abort();
  }, [entries]);

  return <main className="content" id="main" tabIndex={-1}>
    <Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Settings" }]} />
    <div className="page-header"><div><div className="eyebrow">Settings · tenant administrator</div><h1>Tenant settings</h1><p className="sub">Configuration shared by this tenant’s operations and documents.</p></div></div>
    {entries.length === 0 ? <div className="note bad"><strong>Tenant administrator access is required.</strong></div> : <>
      <label className="settings-find"><Icon name="search" /><span className="sr-only">Find a setting</span><input type="search" placeholder="Find a setting…" value={query} onChange={event => setQuery(event.target.value)} /></label>
      {groups.map(group => {
        const items = shown.filter(entry => entry.group === group);
        return items.length > 0 && <section className="settings-group" aria-labelledby={`settings-${group.replace(/\W+/g, "-").toLowerCase()}`} key={group}>
          <h2 id={`settings-${group.replace(/\W+/g, "-").toLowerCase()}`}>{group}</h2>
          <div className="settings-grid">{items.map(entry => <Link className="setting-card" to={entry.path} key={entry.path}>
            <span className="setting-icon"><Icon name={entry.icon} /></span><span className="name">{entry.name}</span><Icon name="chev" className="go" />
            <span className="desc">{entry.description}</span>{entry.currentValue && <span className="value">{values[entry.path] ?? "—"}</span>}
          </Link>)}</div>
        </section>;
      })}
      {shown.length === 0 && <p className="settings-empty">No setting matches. Try a shorter word, for example “mail” or “claim”.</p>}
    </>}
  </main>;
}
