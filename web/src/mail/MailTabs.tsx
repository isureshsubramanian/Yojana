import { NavLink } from "react-router";
import { useSession } from "../session/session";
import { DESIGNER_ROLES, SENDER_ROLES, holds } from "./format";

/** Templates · Sent log · Branding and sender: each tab shows only to someone who can open it. */
export function MailTabs({ templates, sent }: { templates?: number | undefined; sent?: number | undefined }) {
  const roles = useSession().roles;
  return <nav className="tabs" aria-label="Customer e-mail">
    {holds(roles, DESIGNER_ROLES) && <NavLink to="/mail/templates" end>Templates{templates !== undefined && <span className="n">{templates.toLocaleString()}</span>}</NavLink>}
    {holds(roles, SENDER_ROLES) && <NavLink to="/mail/sent">Sent log{sent !== undefined && <span className="n">{sent.toLocaleString()}</span>}</NavLink>}
    {roles.includes("tenant_administrator") && <NavLink to="/settings/company">Branding and sender</NavLink>}
  </nav>;
}
