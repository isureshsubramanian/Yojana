import { useSession } from "../session/session";
import { KnowledgeScreenChanges } from "./KnowledgeScreenChanges";

export function Component() {
  const permitted = useSession().roles.includes("tenant_administrator");
  return <main className="content" id="main" tabIndex={-1}>
    <div className="page-header"><div><div className="eyebrow">Settings · Data and knowledge</div><h1>Knowledge screen changes</h1><p className="sub">Mark a changed screen so published guidance written against it returns to review.</p></div></div>
    {permitted ? <KnowledgeScreenChanges /> : <div className="note bad"><strong>Tenant administrator access is required.</strong></div>}
  </main>;
}
