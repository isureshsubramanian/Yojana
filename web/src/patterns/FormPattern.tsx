import { useState } from "react";
import { Link } from "react-router";
import { ScanInput, SelectionPreview, Shortcut, StatusBadge, useGridState, useModEnter, type PreviewOutcome } from "../components";
import { useSession } from "../session/session";
import { Icon } from "../shell/Icons";
import { Breadcrumbs } from "../shell/PageHeader";
import { previewPage } from "./fixtures";
import { Rail } from "../shell/Drawer";

/** Form pattern: change on the left, the command's own preview on the right with find-within, routing in the rail. */
export function Component() {
  const session = useSession();
  const [query, setQuery] = useState("");
  const [outcome, setOutcome] = useState<PreviewOutcome | null>(null);
  const grid = useGridState("pattern-selection-preview", session.user.id, { key: "priority", dir: "asc" }, 50);
  const [submitted, setSubmitted] = useState(false);
  const preview = previewPage(query, outcome, grid.page);
  const submit = () => setSubmitted(true);
  useModEnter(submit, submitted);
  return (
    <>
      <main className="content" id="main" tabIndex={-1}>
        <Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Patterns", to: "/patterns" }, { label: "Corrections", to: "/corrections" }, { label: "Amend entitlement" }]} />
        <div className="page-header">
          <div>
            <div className="eyebrow">Entitlement command · preview and approve</div>
            <h1>Amend entitlement</h1>
            <p className="sub">Delivery DN-2026-018432 · 214 units · one transaction, one approval</p>
          </div>
          <div className="actions">
            <Link className="btn ghost" to="/patterns">Discard</Link>
            <button type="button" className="btn">Save draft</button>
            <button type="button" className="btn primary" disabled={submitted} onClick={submit}>{submitted ? "Submitted" : "Submit for approval"}<Shortcut keys="mod+enter" /></button>
          </div>
        </div>
        <div className="cols-2" style={{ gridTemplateColumns: "minmax(0, 2fr) minmax(0, 3fr)" }}>
          <section className="card" aria-labelledby="h-change">
            <div className="card-header"><h2 id="h-change">Change</h2><span className="meta">Step 1 of 2</span></div>
            <div className="card-body">
              <div className="form-grid">
                <div className="field span">
                  <label htmlFor="f-set">Selection set</label>
                  <ScanInput id="f-set" value="Delivery DN-2026-018432" onChange={() => undefined} onSubmit={() => undefined} ariaLabel="Selection set" describedBy="f-set-help" hint={<StatusBadge tone="ok">214 units</StatusBadge>} />
                  <span className="help" id="f-set-help">Document, item, date range, pasted serial list or serial range (2506-493445 → 2506-493468).</span>
                </div>
                <div className="field"><label htmlFor="f-what">What changes</label><select id="f-what" className="select" defaultValue="End date"><option>End date</option><option>Term (months)</option><option>Coverage family</option><option>Start date</option></select></div>
                <div className="field"><label htmlFor="f-end">New end date</label><input id="f-end" className="input" type="date" defaultValue="2028-10-02" /></div>
                <div className="field"><label htmlFor="f-reason">Reason code</label><select id="f-reason" className="select" defaultValue="seller_error"><option>seller_error</option><option>purchase</option><option>goodwill</option><option>correction</option><option>dispute_settlement</option></select></div>
                <div className="field"><label htmlFor="f-doc">Source document</label><input id="f-doc" className="input" defaultValue="CR-2026-0096 · credit note" /></div>
                <div className="field span"><label htmlFor="f-note">Note</label><textarea id="f-note" className="textarea" defaultValue="Delivery left Leeds on 2026-09-01 but the customer received the goods on 2026-10-02 after a carrier hold; the warranty start follows the delivery date on the signed POD." /></div>
                <div className="field span">
                  <span className="lbl">Evidence</span>
                  <table className="table dense" style={{ border: "1px solid var(--border)", borderRadius: "var(--radius-s)" }}>
                    <colgroup><col style={{ width: "64%" }} /><col style={{ width: "22%" }} /><col style={{ width: "14%" }} /></colgroup>
                    <tbody>
                      <tr><td className="primary">POD-DN-2026-018432.pdf</td><td className="muted">212 KB</td><td className="actions"><button type="button" className="icon-btn" aria-label="Remove"><Icon name="x" /></button></td></tr>
                      <tr><td className="primary">carrier-hold-notice.eml</td><td className="muted">48 KB</td><td className="actions"><button type="button" className="icon-btn" aria-label="Remove"><Icon name="x" /></button></td></tr>
                    </tbody>
                  </table>
                </div>
                <div className="span note warn"><strong>Approval.</strong>&nbsp;seller_error with 31 days changed exceeds the 30-day service-desk threshold; a service manager must approve. 3 units have a warranty certificate issued, so a customer notification reference is required before commit.</div>
              </div>
            </div>
          </section>
          <SelectionPreview
            preview={preview}
            currentLabel="Current"
            nextLabel="New end"
            changeLabel="Amend"
            query={query}
            outcome={outcome}
            onQuery={(q) => { setQuery(q); grid.reset(); }}
            onOutcome={(o) => { setOutcome(o); grid.reset(); }}
            onPage={grid.onPage}
            onDownload={() => undefined}
            grid={grid}
            gridId="pattern-selection-preview"
            userId={session.user.id}
          />
        </div>
      </main>
      <Rail label="Approval routing">
        <div className="section"><h3>Routing</h3><div className="kv"><span className="k">Initiator</span><span className="v">R. Okafor</span><span className="k">Role</span><span className="v">Service manager</span><span className="k">Approver</span><span className="v">Service manager</span><span className="k">Finance review</span><span className="v muted">Not required</span><span className="k">Days changed</span><span className="v">31</span><span className="k">Threshold</span><span className="v">30 · service desk</span></div></div>
        <div className="section"><h3>What will be recorded</h3><div className="job"><div><div className="t">entitlement.amended</div><div className="s">{preview.summary.change} events · before and after</div></div><StatusBadge tone="neutral">{preview.summary.change}</StatusBadge></div><div className="job"><div><div className="t">Warranty certificate</div><div className="s">New version, reason entitlement_change</div></div><StatusBadge tone="neutral">3</StatusBadge></div><div className="job"><div><div className="t">Approval record</div><div className="s">Initiator, approver, reason, evidence</div></div><StatusBadge tone="neutral">1</StatusBadge></div></div>
        <div className="section"><h3>Rules applied</h3><p className="muted">Reductions after a certificate need a customer notification; units with open claims are blocked until closed; skipped units keep their current entitlement unchanged.</p></div>
      </Rail>
    </>
  );
}
