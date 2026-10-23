import { useState } from "react";
import { Link, NavLink, Outlet, useLocation, useNavigate } from "react-router";
import { useSession } from "../session/session";
import { Icon } from "../shell/Icons";
import { Breadcrumbs } from "../shell/PageHeader";
import { settingsRegistry, visibleSettings, type SettingsGroup, type SettingsRegistryEntry } from "./registry";
import { filterSettings } from "./registryModel";

const groups: SettingsGroup[] = ["Organisation", "Documents", "Service operations", "Data and knowledge"];

export function Component() {
  const location = useLocation();
  const landing = location.pathname === "/settings" || location.pathname === "/settings/";
  const current = settingsRegistry.find(entry => entry.path === location.pathname);
  if (landing) return <Outlet />;
  return <div className="settings-route content">
    <Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Settings", to: "/settings" }, { label: current?.name ?? "Setting not found" }]} />
    <div className="settings-layout">
      <SettingsNavigation currentPath={location.pathname} />
      <div className="settings-body"><Outlet /></div>
    </div>
  </div>;
}

export function SettingsNavigation({ currentPath }: { currentPath: string }) {
  const navigate = useNavigate();
  const entries = visibleSettings(useSession().roles);
  const [query, setQuery] = useState("");
  const shown = filterSettings(entries, query);
  const grouped = (group: SettingsGroup) => shown.filter(entry => entry.group === group);
  return <>
    <label className="settings-mobile-select"><span>Settings</span><select value={currentPath} onChange={event => navigate(event.target.value)}>
      <option value="/settings">All settings</option>
      {groups.map(group => <optgroup label={group} key={group}>{entries.filter(entry => entry.group === group).map(entry => <option value={entry.path} key={entry.path}>{entry.name}</option>)}</optgroup>)}
    </select></label>
    <nav className="settings-nav" aria-label="Settings">
      <Link className="all" to="/settings"><Icon name="chev" />All settings</Link>
      <label className="find"><Icon name="search" /><span className="sr-only">Find a setting</span><input type="search" placeholder="Find a setting" value={query} onChange={event => setQuery(event.target.value)} /></label>
      {groups.map(group => grouped(group).length > 0 && <SettingsNavigationGroup key={group} group={group} entries={grouped(group)} currentPath={currentPath} />)}
      {shown.length === 0 && <p className="settings-nav-empty">No settings match.</p>}
    </nav>
  </>;
}

function SettingsNavigationGroup({ group, entries, currentPath }: { group: SettingsGroup; entries: readonly SettingsRegistryEntry[]; currentPath: string }) {
  return <div className="settings-nav-group"><div className="grp">{group}</div>{entries.map(entry => <NavLink className="page" to={entry.path} aria-current={currentPath === entry.path ? "page" : undefined} key={entry.path}>{entry.name}</NavLink>)}</div>;
}
