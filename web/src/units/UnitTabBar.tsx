import { Link } from "react-router";
import { unitTab } from "../entitlements/coverItems";
export function UnitTabBar({ search, counts }: { search: URLSearchParams; counts: Record<string, string | number | undefined> }) {
  const selected = unitTab(search.get("tab"));
  return <nav className="tabs unit-tabs" aria-label="Unit record sections">{[
    ["history", "History"], ["entitlements", "Entitlements"], ["whats-covered", "What's covered"], ["cover", "Cover since sale"],
    ["identifiers", "Identifiers"], ["custody", "Custody and ownership"], ["asbuilt", "As-built"],
  ].map(([value, label]) => { const next = new URLSearchParams(search); next.set("tab", value!); return <Link key={value} to={`?${next}`} aria-current={selected === value ? "page" : undefined}>{label} <span className="muted">{counts[value!] ?? "…"}</span></Link>; })}</nav>;
}
