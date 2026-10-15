import { NavLink } from "react-router";
import { useSession } from "../session/session";
import { Icon } from "./Icons";
import { navigation, type NavEntry } from "./navigation";

function visible(entry: NavEntry, roles: string[]): boolean {
  return !entry.roles || entry.roles.some((r) => roles.includes(r));
}

export function SideNav() {
  const session = useSession();
  const entries = navigation.filter((e) => visible(e, session.roles));
  let section: NavEntry["section"] | undefined;
  return (
    <nav className="sidenav" aria-label="Primary">
      {entries.map((e) => {
        const heading = e.section && e.section !== section ? <div className="section" key={`s-${e.section}`}>{e.section}</div> : null;
        section = e.section ?? section;
        return (
          <FragmentWithKey key={e.path}>
            {heading}
            <NavLink className="item" to={e.path} end={e.path === "/"}>
              <Icon name={e.icon} /><span>{e.label}</span>
            </NavLink>
          </FragmentWithKey>
        );
      })}
      <div className="foot">{session.tenant?.code ?? ""}<br />Yojana 0.1</div>
    </nav>
  );
}

function FragmentWithKey({ children }: { children: React.ReactNode }) {
  return <>{children}</>;
}
