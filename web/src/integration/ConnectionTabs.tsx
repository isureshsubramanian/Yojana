import { Link } from "react-router";

export function ConnectionTabs({ connectionId, active, showSignIn }: { connectionId: string; active: "sign-in" | "verification"; showSignIn: boolean }) {
  return <nav className="tabs subnav erp-change-tabs" aria-label="Connection">
    <Link to={`/integration?connection=${connectionId}`}>Health</Link>
    {showSignIn && <Link to={`/integration/connections/${connectionId}/sign-in`} aria-current={active === "sign-in" ? "page" : undefined}>Sign-in</Link>}
    <Link to={`/integration/connections/${connectionId}/verification`} aria-current={active === "verification" ? "page" : undefined}>Verification record</Link>
    <Link to={`/integration/connections?connection=${connectionId}`}>Settings</Link>
  </nav>;
}
