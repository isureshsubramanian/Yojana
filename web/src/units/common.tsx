import type { ReactNode } from "react";
import { Link } from "react-router";
import { ApiError } from "../api/client";
import { useSession } from "../session/session";

/** Key/value rows for the drawer and state strips. */
export function Kv({ rows }: { rows: [string, ReactNode][] }) {
  return (
    <div className="kv">
      {rows.map(([k, v]) => (
        <KvRow key={k} label={k} value={v} />
      ))}
    </div>
  );
}

function KvRow({ label, value }: { label: string; value: ReactNode }) {
  const empty = value === null || value === undefined || value === "" || value === "—";
  // A label or a plain value may be cut off by the column; the title keeps the full text one hover away.
  const full = typeof value === "string" || typeof value === "number" ? String(value) : undefined;
  return (
    <>
      <span className="k" title={label}>{label}</span>
      <span className={empty ? "v muted" : "v"} title={empty ? undefined : full}>{empty ? "—" : value}</span>
    </>
  );
}

export function Section({ title, children }: { title: ReactNode; children: ReactNode }) {
  return <div className="section"><h3>{title}</h3>{children}</div>;
}

/** A refusal (409 problem) or any other failure, in the form's own words. */
export function ErrorNote({ error }: { error: unknown }) {
  if (!error) return null;
  const message = error instanceof Error ? error.message : String(error);
  const status = error instanceof ApiError ? error.status : null;
  const rule = error instanceof ApiError ? error.rule : null;
  const step = error instanceof ApiError ? error.unblockingStep : null;
  return <div className="form-error" role="alert">{status === 409 ? `Refused${rule ? ` · ${rule.replace(/_/g, " ")}` : ""} · ` : ""}{message}{step ? ` To unblock: ${step}.` : ""}</div>;
}

export function UnitLink({ unitId, children, className = "serial" }: { unitId: string; children: ReactNode; className?: string }) {
  return <Link className={className} to={`/units/${unitId}`}>{children}</Link>;
}

/**
 * The signed-in user's reference for display and for the actorRef field commands still carry. The server ignores the
 * body's actorRef and records the signed-in user's id from the claims (SerialisationEndpoints.Actor).
 */
export function useActor(): string {
  const session = useSession();
  return session.user.email || session.user.displayName;
}

export function Figure({ label, value, tone }: { label: string; value: ReactNode; tone?: "ok" | "warn" | "bad" | undefined }) {
  return <span><span className="k">{label}</span><span className={`v${tone ? ` ${tone}-text` : ""}`}>{value}</span></span>;
}
