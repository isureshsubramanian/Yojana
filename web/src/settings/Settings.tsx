import { useEffect, useState } from "react";
import { Link } from "react-router";
import { useSession } from "../session/session";
import { Icon } from "../shell/Icons";
import { Breadcrumbs } from "../shell/PageHeader";
import { ErrorNote } from "../units/common";
import { numberingApi, type NumberingMode, type NumberingSeriesSetting } from "./numberingApi";

const sections = [
  ["Company", "Footer line, sender name and reply-to on every e-mail", "/settings/company"],
  ["Users", "Staff memberships, invitations and tenant roles", "/settings/users"],
  ["Sign-in policy", "Password reset links, temporary passwords and invitations", "/settings/sign-in-policy"],
  ["Time zone", "Document time plus issuing-entity overrides", "/settings/time-zone"],
  ["Import files", "Active storage and long-term archive", "/settings/import-files"],
  ["Currencies", "Working and accepted currencies", "/settings/currencies"],
  ["Supplier recovery", "Claim lines and vendor answer timing", "/settings/recovery"],
  ["Claims risk rules", "Second-review checks, thresholds and reviewer role", "/settings/claim-risk"],
  ["Refurbishment policy", "Identity outcomes and cycle limits", "/settings/refurbishment"],
  ["Workbench choices", "Correction choices and permissions", "/settings/workbench-choices"],
  ["Certificates", "Issue point plus date and time presentation", "/settings/certificates"],
] as const;

export function Component() {
  const permitted = useSession().roles.includes("tenant_administrator");
  const [settings, setSettings] = useState<NumberingSeriesSetting[]>([]);
  const [saving, setSaving] = useState<string | null>(null);
  const [error, setError] = useState<unknown>(null);

  useEffect(() => {
    if (!permitted) return;
    const controller = new AbortController();
    numberingApi.settings(controller.signal).then((view) => setSettings(view.series))
      .catch((reason) => { if (!(reason instanceof DOMException && reason.name === "AbortError")) setError(reason); });
    return () => controller.abort();
  }, [permitted]);

  const change = async (series: string, mode: NumberingMode) => {
    setSaving(series); setError(null);
    try {
      const result = await numberingApi.setMode(series, mode);
      setSettings((current) => current.map((row) => row.series === series ? result.setting : row));
    } catch (reason) { setError(reason); }
    finally { setSaving(null); }
  };

  return <main className="content" id="main" tabIndex={-1}>
    <Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Settings" }]} />
    <div className="page-header"><div><div className="eyebrow">Settings · tenant administrator</div><h1>Tenant settings</h1><p className="sub">Configuration shared by this tenant’s operations and documents.</p></div></div>
    {!permitted && <div className="note bad"><strong>Tenant administrator access is required.</strong></div>}
    <ErrorNote error={error} />
    {permitted && <div className="stack">
      <section className="card" aria-labelledby="numbering-settings"><div className="card-header"><h2 id="numbering-settings">Document numbering</h2><span className="meta">per series</span></div><div className="card-body stack">
        <div className="note"><strong>Sequence is the operational default.</strong>&nbsp;It lets creators work in parallel, but a rolled-back transaction may leave a number unused. Choose gap-free only where an audit requirement outweighs the concurrency cost.</div>
        {settings.length === 0 && <p className="muted">Loading document-number settings…</p>}
        {settings.map((row) => <div className="field" key={row.series}>
          <span className="label">{row.label} · <span className="mono">{row.example}</span></span>
          <div className="cluster">
            <label className="radiorow"><input type="radio" name={`numbering-${row.series}`} checked={row.mode === "sequence"} disabled={saving !== null} onChange={() => void change(row.series, "sequence")} /><span><strong>Sequence</strong><span className="help">Parallel allocation; rollback gaps are accepted.</span></span></label>
            <label className="radiorow"><input type="radio" name={`numbering-${row.series}`} checked={row.mode === "gap_free"} disabled={saving !== null} onChange={() => void change(row.series, "gap_free")} /><span><strong>Gap-free</strong><span className="help">Creators queue; committed numbers remain consecutive.</span></span></label>
          </div>
          <span className="help">{saving === row.series ? "Saving…" : row.gapBehaviour}</span>
        </div>)}
      </div></section>
      <section className="card" aria-labelledby="other-settings"><div className="card-header"><h2 id="other-settings">Other settings</h2></div><div className="card-body stack">
        {sections.map(([label, description, to]) => <Link className="btn" to={to} key={to}><Icon name="sliders" /><span>{label} · {description}</span></Link>)}
      </div></section>
    </div>}
  </main>;
}
