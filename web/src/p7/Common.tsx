import { useEffect, useState } from "react";
import { NavLink, Outlet, useLocation } from "react-router";
import { ModeMenuItems } from "../theme/ModeMenuItems";
import { useSession } from "../session/session";
import { p7Api } from "./api";

export function ExportButtons({ path, disabled = false }: { path: string; disabled?: boolean }) {
  return <>{path === "/api/imports/export" && <NavLink className="btn" to="/imports/history">Upload history</NavLink>}<span className="export-pair" aria-label="Export"><a className={`btn${disabled ? " disabled" : ""}`} href={disabled ? undefined : `${path}${path.includes("?") ? "&" : "?"}format=csv`} aria-disabled={disabled}>CSV</a><a className={`btn${disabled ? " disabled" : ""}`} href={disabled ? undefined : `${path}${path.includes("?") ? "&" : "?"}format=xlsx`} aria-disabled={disabled}>Excel</a></span></>;
}

export function PrimaryButton({ children, onClick, disabled, busy }: { children: React.ReactNode; onClick: () => void; disabled?: boolean; busy?: boolean }) {
  return <button type="button" className="btn primary" disabled={disabled || busy} aria-busy={busy} onClick={onClick}>{busy ? "Working…" : children}</button>;
}

export function EmptyState({ title, children }: { title: string; children: React.ReactNode }) { return <div className="p7-empty"><strong>{title}</strong><span>{children}</span></div>; }

export function PortalFrame() {
  const session = useSession();
  const location = useLocation();
  const claimsSelected = new URLSearchParams(location.search).get("focus") === "claims";
  const [organisation, setOrganisation] = useState<string | null>(null);
  const [denied, setDenied] = useState(false);
  useEffect(() => { const controller = new AbortController(); void p7Api.portalContext(controller.signal).then((context) => setOrganisation(context.organisationName)).catch((reason) => { if (!(reason instanceof DOMException && reason.name === "AbortError")) setDenied(true); }); return () => controller.abort(); }, []);
  if (denied) return <div className="portal-frame"><main className="portal-content"><EmptyState title="Portal access unavailable">Sign in with your organisation's portal account.</EmptyState></main></div>;
  if (!organisation) return <div className="portal-frame"><main className="portal-content"><div className="p7-empty">Loading portal…</div></main></div>;
  return <div className="portal-frame"><header className="portal-top"><NavLink className="portal-brand" to="/portal/lookup"><span className="mark">Y</span><span><strong>{organisation} Service portal</strong><small>Serials, cover and service</small></span></NavLink><nav aria-label="Portal"><NavLink className={() => location.pathname === "/portal/lookup" && !claimsSelected ? "active" : ""} to="/portal/lookup">Lookup</NavLink><NavLink to="/portal/installed-base">Installed base</NavLink><NavLink to="/portal/where-used">Where-used</NavLink><NavLink className={() => claimsSelected ? "active" : ""} to="/portal/lookup?focus=claims">Claims</NavLink></nav><div className="portal-account"><span>{organisation}</span><small>{session.user.displayName}</small></div><details className="portal-mode"><summary>Appearance</summary><div className="menu"><ModeMenuItems /></div></details></header><Outlet /></div>;
}

export function PortalPage({ eyebrow, title, sub, actions, children }: { eyebrow: string; title: React.ReactNode; sub?: React.ReactNode; actions?: React.ReactNode; children: React.ReactNode }) {
  return <main className="portal-content" id="main" tabIndex={-1}><div className="page-header"><div><div className="eyebrow">{eyebrow}</div><h1>{title}</h1>{sub && <p className="sub">{sub}</p>}</div>{actions && <div className="actions">{actions}</div>}</div>{children}</main>;
}

export function FieldLabel({ children }: { children: React.ReactNode }) { return <span className="lbl">{children}</span>; }
