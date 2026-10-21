import { Link } from "react-router";

export function ClaimsTabs({ current, riskCount }: { current: "claims" | "risk"; riskCount?: number | undefined }) {
  return <nav className="tabs claims-tabs" aria-label="Claims">
    <Link to="/claims" aria-current={current === "claims" ? "page" : undefined}>Claims</Link>
    <Link to="/claims/risk-review" aria-current={current === "risk" ? "page" : undefined}>Risk review{riskCount !== undefined && <span className={`n${riskCount > 0 ? " warn" : ""}`}>{riskCount.toLocaleString()}</span>}</Link>
  </nav>;
}
