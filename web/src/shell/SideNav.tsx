import { Fragment, useEffect, useRef, useState, type KeyboardEvent } from "react";
import { NavLink, useLocation } from "react-router";
import { useSession } from "../session/session";
import { Icon } from "./Icons";
import { entryFor, groupHasWaiting, visibleNavigation, type NavCountSource, type NavEntry, type NavGroup } from "./navigation";

const isEntryActive = (entry: NavEntry, pathname: string) => entryFor(pathname)?.path === entry.path;
const isGroupActive = (group: NavGroup, pathname: string) => group.items.some(entry => isEntryActive(entry, pathname));
const countText = (count: number) => count > 99 ? "99+" : count.toLocaleString();

export function SideNav({ counts = {} }: { counts?: Readonly<Partial<Record<NavCountSource, number>>> }) {
  const session = useSession();
  const { pathname } = useLocation();
  const groups = visibleNavigation(session.roles);
  const [open, setOpen] = useState<string | null>(null);
  const rail = useRef<HTMLElement>(null);
  const buttons = useRef(new Map<string, HTMLButtonElement>());
  const flyout = useRef<HTMLElement>(null);

  useEffect(() => {
    if (!open) return;
    flyout.current?.querySelector<HTMLAnchorElement>("a")?.focus();
    const outside = (event: MouseEvent) => {
      const target = event.target as Node;
      if (!rail.current?.contains(target) && !flyout.current?.contains(target)) setOpen(null);
    };
    document.addEventListener("mousedown", outside);
    return () => document.removeEventListener("mousedown", outside);
  }, [open]);

  const closeToButton = (key: string) => {
    setOpen(null);
    requestAnimationFrame(() => buttons.current.get(key)?.focus());
  };

  const onFlyoutKey = (event: KeyboardEvent<HTMLElement>, group: NavGroup) => {
    const links = [...event.currentTarget.querySelectorAll<HTMLAnchorElement>("a")];
    const current = links.indexOf(document.activeElement as HTMLAnchorElement);
    let next: number | null = null;
    if (event.key === "ArrowDown") next = (current + 1) % links.length;
    if (event.key === "ArrowUp") next = (current - 1 + links.length) % links.length;
    if (event.key === "Home") next = 0;
    if (event.key === "End") next = links.length - 1;
    if (event.key === "Escape") { event.preventDefault(); closeToButton(group.key); return; }
    if (next !== null) { event.preventDefault(); links[next]?.focus(); }
  };

  const top = groups.filter(group => group.position !== "bottom");
  const bottom = groups.filter(group => group.position === "bottom");
  const renderGroup = (group: NavGroup) => {
    const waiting = groupHasWaiting(group, counts);
    const active = isGroupActive(group, pathname);
    const label = `${group.label}${waiting ? ", has waiting work" : ""}`;
    if (group.items.length === 1) {
      const entry = group.items[0]!;
      return <NavLink className="item" data-active={active} to={entry.path} title={label} aria-label={label} end={entry.path === "/"} key={group.key}><Icon name={group.icon} /><span>{group.label}</span>{waiting && <span className="nav-waiting-dot" aria-hidden="true" />}</NavLink>;
    }
    const expanded = open === group.key;
    return <button type="button" className="item" data-active={active} aria-haspopup="true" aria-expanded={expanded} aria-controls={`nav-flyout-${group.key}`} title={label} aria-label={label} key={group.key}
      ref={node => { if (node) buttons.current.set(group.key, node); else buttons.current.delete(group.key); }}
      onClick={() => setOpen(current => current === group.key ? null : group.key)}>
      <Icon name={group.icon} /><span>{group.label}</span>{waiting && <span className="nav-waiting-dot" aria-hidden="true" />}
    </button>;
  };
  const current = groups.find(group => group.key === open);

  return <>
    <nav className="sidenav grouped" aria-label="Primary" ref={rail}>
      {top.map((group, index) => <Fragment key={group.key}>{index === 1 && <div className="section">Groups</div>}{renderGroup(group)}</Fragment>)}
      <span className="grow" />
      {bottom.map(renderGroup)}
    </nav>
    {current && <nav className="nav-flyout" id={`nav-flyout-${current.key}`} aria-label={`${current.label} screens`} ref={flyout} onKeyDown={event => onFlyoutKey(event, current)}>
      <div className="nav-flyout-heading">{current.label}</div>
      {current.items.map(entry => <NavLink to={entry.path} key={entry.path} aria-current={isEntryActive(entry, pathname) ? "page" : undefined} onClick={() => setOpen(null)}>
        <Icon name={entry.icon} /><span>{entry.label}</span>{entry.countSource && (counts[entry.countSource] ?? 0) > 0 && <span className="count">{countText(counts[entry.countSource]!)}</span>}
      </NavLink>)}
    </nav>}
  </>;
}
