import { useEffect, useRef, useState } from "react";
import { Link, useNavigate, useRouteLoaderData } from "react-router";
import { api } from "../api/client";
import type { Health, Session } from "../api/types";
import { ROOT_ROUTE_ID, initials, roleLabel } from "../session/session";
import { ModeMenuItems } from "../theme/ModeMenuItems";
import { openSearch } from "../components/search/searchStore";
import { Icon } from "./Icons";
import { toggleDrawer, useDrawer } from "./drawerStore";

const HEALTH_REFRESH_MS = 60_000;

export function TopBar({ bare = false }: { bare?: boolean }) {
  const session = useRouteLoaderData(ROOT_ROUTE_ID) as Session | undefined;
  const drawer = useDrawer();
  return (
    <header className="topbar" role="banner">
      <Link className="brand" to="/"><span className="mark" aria-hidden="true">Y</span>Yojana<span className="product-sub">Serial &amp; Warranty</span></Link>
      {!bare && session?.tenant && (
        <>
          <span className="sep" />
          <Link className="pill link" to="/tenants" title="Switch tenant">{session.tenant.name}<Icon name="chev" /></Link>
          <HealthPill />
        </>
      )}
      <span className="grow" />
      {!bare && (
        <>
          <button type="button" className="pill quiet" onClick={() => openSearch()} aria-label="Search everything" aria-haspopup="dialog"><Icon name="search" />Search<kbd className="key">⌘K</kbd></button>
          <Link className="icon-btn" to="/corrections" aria-label="Approvals"><Icon name="bell" /></Link>
          <button type="button" className="icon-btn" onClick={toggleDrawer} aria-label="Details panel" aria-pressed={drawer.open} title="Details panel"><Icon name="panel" /></button>
        </>
      )}
      {session && <UserMenu session={session} />}
    </header>
  );
}

/** Connection health for the header. The platform answers "none" until a connector module reports (MOD-14, P8). */
function HealthPill() {
  const [health, setHealth] = useState<Health | null>(null);
  useEffect(() => {
    const controller = new AbortController();
    const load = () => api<Health>("/api/shell/health", { signal: controller.signal }).then(setHealth).catch(() => undefined);
    load();
    const timer = window.setInterval(load, HEALTH_REFRESH_MS);
    return () => { controller.abort(); window.clearInterval(timer); };
  }, []);
  if (!health) return null;
  const dot = health.status === "ok" ? "dot ok" : health.status === "warn" ? "dot warn" : health.status === "bad" ? "dot bad" : "dot";
  return (
    <span className="pill quiet" aria-live="polite" title={health.detail ?? undefined}>
      <span className={dot} aria-hidden="true" />{health.label}
    </span>
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
          <a className="mi" role="menuitem" href="/notices" target="_blank" rel="noopener">Third-party notices<span className="k">↗</span></a>
          <button type="button" className="mi" role="menuitem" onClick={signOut}>Sign out<span className="k">{session.user.email}</span></button>
        </div>
      )}
    </div>
  );
}
