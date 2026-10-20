import { useState } from "react";
import { PasswordChangeForm } from "../security/PasswordChangeForm";
import { Breadcrumbs, PageHeader } from "../shell/PageHeader";

export function Component() {
  const [saved, setSaved] = useState(false);
  return <main className="content" id="main" tabIndex={-1}>
    <Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Your account" }, { label: "Security" }]} />
    <PageHeader eyebrow="Your account" title="Security" sub="Manage the password used for your Yojana account." />
    {saved && <div className="note ok"><strong>Password changed.</strong>&nbsp;You stayed signed in here and every other session ended.</div>}
    <div className="stack top" style={{ maxWidth: 720 }}><section className="card" aria-labelledby="account-password"><div className="card-header"><h2 id="account-password">Password</h2><span className="meta">Never expires</span></div><div className="card-body">
      <PasswordChangeForm onChanged={() => setSaved(true)} />
    </div></section></div>
  </main>;
}
