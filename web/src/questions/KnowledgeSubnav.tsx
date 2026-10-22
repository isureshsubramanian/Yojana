import { Link } from "react-router";

export function KnowledgeSubnav({ current }: { current: "mine" | "knowledge" | "review" | "training" | "reports" }) {
  return <nav className="tabs questions-subnav" aria-label="Questions and knowledge">
    <Link to="/questions" aria-current={current === "mine" ? "page" : undefined}>My questions</Link>
    <Link to="/knowledge" aria-current={current === "knowledge" ? "page" : undefined}>Knowledge base</Link>
    <Link to="/knowledge/entries/review" aria-current={current === "review" ? "page" : undefined}>Entries to review</Link>
    <Link to="/knowledge/training" aria-current={current === "training" ? "page" : undefined}>Training paths</Link>
    <Link to="/knowledge/reports" aria-current={current === "reports" ? "page" : undefined}>Reports</Link>
  </nav>;
}
