import { useEffect, useRef, useState } from "react";
import { Link, useParams, useSearchParams } from "react-router";
import { Breadcrumbs, PageHeader } from "../shell/PageHeader";
import { ErrorNote } from "../units/common";
import { date, RoleAccess } from "./AdminCommon";
import { signInApi } from "./erpChangeApi";

export function Component() { return <RoleAccess><SignInCallback /></RoleAccess>; }

/** The ERP redirects the administrator's browser here with a one-time code and the state the module issued. */
function SignInCallback() {
  const connectionId = useParams().connectionId ?? "";
  const [params] = useSearchParams();
  const code = params.get("code"); const state = params.get("state"); const refused = params.get("error");
  const [outcome, setOutcome] = useState<"working" | "done" | "failed">(code && state && !refused ? "working" : "failed");
  const [error, setError] = useState<unknown>(null); const [expires, setExpires] = useState<string | null>(null);
  const sent = useRef(false);
  useEffect(() => {
    if (!code || !state || refused || sent.current) return;
    sent.current = true;
    // Drop the one-time code and state from the address bar before anything else can read or bookmark it.
    window.history.replaceState(null, "", window.location.pathname);
    signInApi.complete(connectionId, code, state).then(result => { setExpires(result.refreshChainExpiresAt); setOutcome("done"); }).catch(reason => { setError(reason); setOutcome("failed"); });
  }, [connectionId, code, state, refused]);
  return <main className="content" id="main" tabIndex={-1}>
    <Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Integration", to: "/integration" }, { label: "Connections", to: "/integration/connections" }, { label: "Sign-in" }]} />
    <PageHeader eyebrow="Connection" title="Signing in" sub="The ERP has sent the API user back to the module." />
    <div className="stack">
      {outcome === "working" && <p role="status">Finishing sign-in…</p>}
      {outcome === "done" && <div className="note ok" role="status"><strong>Signed in.</strong> Sync resumes and waiting documents are sent.{expires ? ` The sign-in expires ${date(expires)}.` : ""}</div>}
      {outcome === "failed" && <div className="note bad" role="alert"><strong>Sign-in did not finish.</strong> {refused ? "The ERP reported that access was not granted." : !error ? "The ERP did not return a sign-in code." : ""}</div>}
      <ErrorNote error={error} />
      <Link className="btn" to={`/integration/connections/${connectionId}/sign-in`}>Back to sign-in</Link>
    </div>
  </main>;
}
