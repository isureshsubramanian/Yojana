import { useEffect, useRef, useState } from "react";
import { Link, useNavigate, useRouteLoaderData } from "react-router";
import { api } from "../api/client";
import type { Session } from "../api/types";
import { ROOT_ROUTE_ID, initials, roleLabel } from "../session/session";
import { ModeMenuItems } from "../theme/ModeMenuItems";
import { setMode, useEffectiveMode } from "../theme/mode";
import { openSearch } from "../components/search/searchStore";
import { Shortcut } from "../components/Shortcut";
import { Icon } from "./Icons";
import { toggleDrawer, useDrawer } from "./drawerStore";
import { integrationApi } from "../integration/api";
import { maySeeIntegrationIndicator } from "../integration/model";
import { pollNotifications, type ShellNotificationItem, type ShellNotificationSource } from "./notificationSource";

const HEALTH_REFRESH_MS = 60_000;

export function TopBar({ bare = false, notificationSources = [] }: { bare?: boolean; notificationSources?: ShellNotificationSource[] }) {
  const session = useRouteLoaderData(ROOT_ROUTE_ID) as Session | undefined;
  const drawer = useDrawer();
  return (
    <header className="topbar" role="banner">
      <Link className="brand" to="/"><span className="mark" aria-hidden="true">Y</span>Yojana<span className="product-sub">Serial &amp; Warranty</span></Link>
      {!bare && session?.tenant && (
        <>
          <span className="sep" />
          <Link className="pill link" to="/tenants" title="Switch tenant">{session.tenant.name}<Icon name="chev" /></Link>
          <IntegrationPill roles={session.roles} />
        </>
      )}
      {!bare && (
        <>
          <button type="button" className="topbar-search" onClick={() => openSearch()} onFocus={() => openSearch()}
            onKeyDown={(event) => {
              if (event.metaKey || event.ctrlKey || event.altKey || event.key.length !== 1 || !event.key.trim()) return;
              event.preventDefault(); openSearch(event.key);
            }} aria-label="Search units, identifiers, documents, entitlements, claims, RMAs, certificates and parties" aria-haspopup="dialog">
            <Icon name="search" /><span className="topbar-search-placeholder">Search units, documents, claims, RMAs…</span><Shortcut keys="mod+k" />
          </button>
          <NotificationBell sources={notificationSources} />
          <ModeToggle />
          <button type="button" className="icon-btn" onClick={toggleDrawer} aria-label="Details panel" aria-pressed={drawer.open} title="Details panel"><Icon name="panel" /></button>
        </>
      )}
      {session && <UserMenu session={session} />}
    </header>
  );
}

const notificationEnvironment = {
  hidden: () => document.visibilityState === "hidden",
  every: (milliseconds: number, callback: () => void) => window.setInterval(callback, milliseconds),
  cancel: (handle: unknown) => window.clearInterval(handle as number),
  onVisibility: (callback: () => void) => { document.addEventListener("visibilitychange", callback); return () => document.removeEventListener("visibilitychange", callback); },
  onFocus: (callback: () => void) => { window.addEventListener("focus", callback); return () => window.removeEventListener("focus", callback); },
};

function NotificationBell({ sources }: { sources: ShellNotificationSource[] }) {
  const [open, setOpen] = useState(false); const [count, setCount] = useState(0);
  const [items, setItems] = useState<ShellNotificationItem[]>([]); const anchor = useRef<HTMLDivElement>(null);
  useEffect(() => {
    const controller = new AbortController();
    const stop = pollNotifications(async () => {
      const summaries = await Promise.all(sources.map(source => source.load(controller.signal)));
      setCount(summaries.reduce((total, summary) => total + summary.count, 0));
      setItems(summaries.flatMap(summary => summary.items).sort((a, b) => b.at.localeCompare(a.at)).slice(0, 5));
    }, notificationEnvironment);
    return () => { controller.abort(); stop(); };
  }, [sources]);
  useEffect(() => {
    if (!open) return;
    const onDown = (event: MouseEvent) => { if (!anchor.current?.contains(event.target as Node)) setOpen(false); };
    const onKey = (event: KeyboardEvent) => { if (event.key === "Escape") setOpen(false); };
    document.addEventListener("mousedown", onDown); document.addEventListener("keydown", onKey);
    return () => { document.removeEventListener("mousedown", onDown); document.removeEventListener("keydown", onKey); };
  }, [open]);
  return <div className="menu-anchor notification-anchor" ref={anchor}><button type="button" className="icon-btn" aria-label={`Notifications${count ? `, ${count} unread` : ""}`} aria-haspopup="menu" aria-expanded={open} onClick={() => setOpen(value => !value)}><Icon name="bell" />{count > 0 && <span className="count">{count > 99 ? "99+" : count}</span>}</button>{open && <div className="menu notification-menu" role="menu" aria-label="Notifications"><div className="group">Questions · {count.toLocaleString()} unread</div>{items.length ? items.map(item => <Link key={item.id} className="notification-item" role="menuitem" to={item.href} onClick={() => setOpen(false)}><span>{item.title}</span><small>{item.detail}</small></Link>) : <div className="notification-empty">You are up to date.</div>}<hr /><Link className="mi" role="menuitem" to="/questions" onClick={() => setOpen(false)}>All my questions<span className="k">{count || "None"}</span></Link><Link className="mi" role="menuitem" to="/corrections" onClick={() => setOpen(false)}>Approvals</Link></div>}</div>;
}

function IntegrationPill({ roles }: { roles: string[] }) {
  const permitted = maySeeIntegrationIndicator(roles);
  const [count, setCount] = useState<number | null>(null);
  useEffect(() => {
    if (!permitted) return;
    const controller = new AbortController();
    const load = () => integrationApi.attention(controller.signal).then((value) => setCount(value.needsAttention)).catch(() => undefined);
    load(); const timer = window.setInterval(load, HEALTH_REFRESH_MS);
    return () => { controller.abort(); window.clearInterval(timer); };
  }, [permitted]);
  if (!permitted || count === null) return null;
  return <Link className="pill quiet link" to="/integration" aria-live="polite"><span className={`dot ${count > 0 ? "warn" : "ok"}`} aria-hidden="true" />Integration: {count > 0 ? `${count.toLocaleString()} need attention` : "ready"}</Link>;
}

/** One-click light/dark flip from what is on screen now; "Match system" stays in the user menu. */
function ModeToggle() {
  const effective = useEffectiveMode();
  const next = effective === "dark" ? "light" : "dark";
  const label = next === "dark" ? "Switch to dark mode" : "Switch to light mode";
  return (
    <button type="button" className="icon-btn" onClick={() => setMode(next)} aria-label={label} title={label}>
      <Icon name={next === "dark" ? "moon" : "sun"} />
    </button>
  );
}

function UserMenu({ session }: { session: Session }) {
  const [open, setOpen] = useState(false);
  const anchor = useRef<HTMLDivElement>(null);
  const navigate = useNavigate();
  useEffect(() => {
    if (!open) return;
    const onDown = (e: MouseEvent) => { if (!anchor.current?.contains(e.target as Node)) setOpen(false); };
    const onKey = (e: KeyboardEvent) => { if (e.key === "Escape") setOpen(false); };
    document.addEventListener("mousedown", onDown);
    document.addEventListener("keydown", onKey);
    return () => { document.removeEventListener("mousedown", onDown); document.removeEventListener("keydown", onKey); };
  }, [open]);
  const primaryRole = session.roles[0] ? roleLabel(session.roles[0]) : "Signed in";
  const signOut = async () => {
    await api<void>("/api/auth/sign-out", { method: "POST" });
    navigate("/sign-in", { replace: true });
  };
  return (
    <div className="menu-anchor" ref={anchor}>
      <button type="button" className="user button" aria-haspopup="menu" aria-expanded={open} onClick={() => setOpen((o) => !o)}>
        <span className="avatar" aria-hidden="true">{initials(session.user.displayName)}</span>
        <span className="who"><span className="n">{session.user.displayName}</span><span className="r">{primaryRole}</span></span>
      </button>
      {open && (
        <div className="menu" role="menu" aria-label="Account">
          <div className="group">Appearance</div>
          <ModeMenuItems />
          <hr />
          <Link className="mi" role="menuitem" to="/account/security" onClick={() => setOpen(false)}>Account security<span className="k">Password</span></Link>
          <Link className="mi" role="menuitem" to="/third-party-notices" onClick={() => setOpen(false)}>Third-party notices</Link>
          <button type="button" className="mi" role="menuitem" onClick={signOut}>Sign out<span className="k">{session.user.email}</span></button>
        </div>
      )}
    </div>
  );
}
