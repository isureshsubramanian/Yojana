import { Link } from "react-router";
import type { ConnectionFeatures } from "./adminTypes";

export function ConnectionTabs({ connectionId, active, features }: { connectionId: string; active: "sign-in" | "extension" | "verification"; features: ConnectionFeatures | null }) {
  return <nav className="tabs subnav erp-change-tabs" aria-label="Connection">
    <Link to={`/integration?connection=${connectionId}`}>Health</Link>
    {features?.interactiveSignIn && <Link to={`/integration/connections/${connectionId}/sign-in`} aria-current={active === "sign-in" ? "page" : undefined}>Sign-in</Link>}
    {features?.tenantSideExtension && <Link to={`/integration/connections/${connectionId}/extension`} aria-current={active === "extension" ? "page" : undefined}>Extension</Link>}
    <Link to={`/integration/connections/${connectionId}/verification`} aria-current={active === "verification" ? "page" : undefined}>Verification record</Link>
    <Link to={`/integration/connections?connection=${connectionId}`}>Settings</Link>
  </nav>;
}
