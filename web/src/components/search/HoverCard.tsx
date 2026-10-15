import { Link } from "react-router";
import type { ResultMetadata } from "../types";

/** The metadata the tenant configured for this record type (Settings › Search › Metadata), filtered by the viewer's disclosure tier server-side. */
export function HoverCard({ metadata }: { metadata: ResultMetadata | null }) {
  if (!metadata) {
    return <aside className="hovercard" aria-label="Preview of the selected result"><p className="cfg">Select a result to see its metadata.</p></aside>;
  }
  return (
    <aside className="hovercard" aria-label="Preview of the selected result">
      <div className="head">
        <div className="eyebrow">{metadata.eyebrow}</div>
        <div className={`t${metadata.titleMono ? " serial" : ""}`}>{metadata.title}</div>
        {metadata.subtitle && <div className="s">{metadata.subtitle}</div>}
      </div>
      <div className="kv">
        {metadata.fields.map((f) => (
          <FieldRow key={f.label} label={f.label} value={f.value} mono={f.mono ?? false} />
        ))}
      </div>
      {metadata.actions.length > 0 && (
        <div className="actions">
          {metadata.actions.map((a) => <Link key={a.href} className={`btn sm${a.primary ? " primary" : ""}`} to={a.href}>{a.label}</Link>)}
        </div>
      )}
      {metadata.note && <p className="cfg">{metadata.note}</p>}
    </aside>
  );
}

function FieldRow({ label, value, mono }: { label: string; value: string | null; mono: boolean }) {
  return (
    <>
      <span className="k">{label}</span>
      <span className={`v${value === null ? " muted" : ""}${mono ? " serial" : ""}`}>{value ?? "—"}</span>
    </>
  );
}
