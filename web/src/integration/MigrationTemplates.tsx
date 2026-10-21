import { useCallback, useEffect, useMemo, useState } from "react";
import { DataGrid, KpiStrip, StatusBadge, type GridColumn } from "../components";
import { useSession } from "../session/session";
import { Rail } from "../shell/Drawer";
import { openDrawer } from "../shell/drawerStore";
import { ErrorNote, Section } from "../units/common";
import { MigrationAccess, MigrationPage } from "./MigrationCommon";
import { migrationApi } from "./migrationApi";
import {
  TEMPLATE_STATES, mayAdministerMigration, templateFamilyLabel,
  templateResolutionLabel, templateStateLabel, templateStateTone, templateTermsLabel,
} from "./migrationModel";
import type {
  PolicyCatalogue, TemplateKeywordRule, TemplateKeywordRules, TemplateMappingDetail,
  TemplateMappingState, TemplateMappingSummary, TemplateMappingSummaryView, TemplatePattern,
  TemplatePatternPage,
} from "./migrationTypes";

const TAKE = 25;

export function Component() { return <MigrationAccess><TemplateScreen /></MigrationAccess>; }

function TemplateScreen() {
  const admin = mayAdministerMigration(useSession().roles);
  const [summary, setSummary] = useState<TemplateMappingSummaryView | null>(null);
  const [catalogue, setCatalogue] = useState<PolicyCatalogue | null>(null);
  const [result, setResult] = useState<TemplatePatternPage | null>(null);
  const [state, setState] = useState<TemplateMappingState | "">("");
  const [deleted, setDeleted] = useState<"" | "false" | "true">("");
  const [pageNumber, setPageNumber] = useState(1); const [cursors, setCursors] = useState<(string | undefined)[]>([undefined]);
  const [selectedPatternKey, setSelectedPatternKey] = useState("");
  const [loading, setLoading] = useState(false); const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null); const [notice, setNotice] = useState("");
  const [revision, setRevision] = useState(0);

  const reload = useCallback(() => setRevision(value => value + 1), []);
  useEffect(() => {
    setPageNumber(1); setCursors([undefined]); setSelectedPatternKey("");
  }, [state, deleted]);
  useEffect(() => {
    const controller = new AbortController(); setLoading(true); setError(null);
    const cursor = cursors[pageNumber - 1];
    Promise.all([
      migrationApi.templatePatterns({ state, ...(deleted ? { deletedInSource: deleted === "true" } : {}), ...(cursor ? { cursor } : {}), take: TAKE }, controller.signal),
      migrationApi.templateSummary(controller.signal), migrationApi.templateCatalogue(controller.signal),
    ]).then(([page, totals, policies]) => {
      if (controller.signal.aborted) return;
      setResult(page); setSummary(totals); setCatalogue(policies);
      setSelectedPatternKey(current => page.items.some(pattern => pattern.patternKey === current) ? current : page.items[0]?.patternKey ?? "");
    }).catch(reason => { if (!controller.signal.aborted) setError(reason); })
      .finally(() => { if (!controller.signal.aborted) setLoading(false); });
    return () => controller.abort();
  }, [state, deleted, pageNumber, cursors, revision]);

  const selected = result?.items.find(pattern => pattern.patternKey === selectedPatternKey) ?? result?.items[0] ?? null;
  const go = (next: number) => {
    if (next < 1 || next > pageNumber + 1 || next === pageNumber) return;
    if (next === pageNumber + 1) {
      if (!result?.nextCursor) return;
      setCursors(current => [...current.slice(0, pageNumber), result.nextCursor!]);
    }
    setPageNumber(next);
  };
  const approvePattern = async (pattern: TemplatePattern) => {
    setBusy(true); setError(null); setNotice("");
    try { const approved = await migrationApi.approveTemplatePattern(pattern.patternKey); setNotice(`${approved.approved.toLocaleString()} templates approved.`); reload(); }
    catch (reason) { setError(reason); } finally { setBusy(false); }
  };
  const proposeAgain = async () => {
    setBusy(true); setError(null); setNotice("");
    try { const proposed = await migrationApi.proposeTemplates(); setNotice(`${proposed.proposed.toLocaleString()} templates proposed · ${proposed.unchanged.toLocaleString()} unchanged.`); reload(); }
    catch (reason) { setError(reason); } finally { setBusy(false); }
  };
  const showTemplate = async (row: TemplateMappingSummary) => {
    openDrawer({ eyebrow: "Contract template", title: row.templateName }, <p className="muted">Loading terms and cover blocks…</p>);
    try {
      const policies = catalogue ?? await migrationApi.templateCatalogue();
      const detail = await migrationApi.templateDetail(row.templateKey);
      openDrawer({ eyebrow: row.sourceReference ? `Contract template · ${row.sourceReference}` : "Contract template", title: row.templateName },
        <TemplateDrawer initial={detail} catalogue={policies} onChanged={reload} />);
    } catch (reason) { openDrawer({ eyebrow: "Contract template", title: row.templateName }, <ErrorNote error={reason} />); }
  };
  const showKeywordRules = async () => {
    openDrawer({ eyebrow: "Contract templates", title: "Keyword rules" }, <p className="muted">Loading tenant rules…</p>);
    try {
      const [rules, policies] = await Promise.all([migrationApi.templateKeywordRules(), catalogue ? Promise.resolve(catalogue) : migrationApi.templateCatalogue()]);
      openDrawer({ eyebrow: "Contract templates", title: "Keyword rules" }, <KeywordRulesForm initial={rules} catalogue={policies} onSaved={reload} />);
    } catch (reason) { openDrawer({ eyebrow: "Contract templates", title: "Keyword rules" }, <ErrorNote error={reason} />); }
  };

  const patternColumns = useMemo<GridColumn<TemplatePattern>[]>(() => [
    { key: "blocks", label: "Cover blocks", width: 47, render: pattern => <span className="primary"><span className="t">{pattern.patternLabel}</span><span className="sub">{pattern.templates.slice(0, 3).map(template => template.templateName).join(" · ")}{pattern.templateCount > 3 ? ` · ${pattern.templateCount - 3} more` : ""}</span></span> },
    { key: "templates", label: "Templates", width: 10, numeric: true, render: pattern => pattern.templateCount.toLocaleString() },
    { key: "contracts", label: "Active contracts", width: 14, numeric: true, render: pattern => pattern.usage.activeContracts.toLocaleString() },
    { key: "serials", label: "Active serials", width: 14, numeric: true, render: pattern => pattern.usage.activeSerials.toLocaleString() },
    { key: "state", label: "State", width: 15, render: pattern => <span className="migration-template-state"><StatusBadge tone={templateStateTone(pattern.state)}>{templateStateLabel(pattern.state)}</StatusBadge>{pattern.state === "waiting" && <button type="button" className="btn sm primary" disabled={busy} onClick={() => void approvePattern(pattern)}>Approve {pattern.templateCount.toLocaleString()}</button>}{pattern.state === "needs_person" && <span className="sub">{pattern.templates.find(template => template.reviewReason)?.reviewReason ?? "Decide each template"}</span>}{pattern.state === "waiting_for_policy_publication" && <span className="sub">Publish the draft policy before loading</span>}</span> },
  ], [busy]);
  const templateColumns = useMemo<GridColumn<TemplateMappingSummary>[]>(() => [
    { key: "name", label: "Template as named in the source", width: 29, render: row => <span className={`primary${row.deletedInSource ? " migration-template-deleted" : ""}`}><span className="t">{row.templateName}</span><span className="sub">{[row.sourceReference ? `Source template ${row.sourceReference}` : null, row.deletedInSource ? "Deleted in the source" : null].filter(Boolean).join(" · ")}</span></span> },
    { key: "terms", label: "Source terms", width: 41, render: row => <span className="wrap">{templateTermsLabel(row.sourceTerms)}</span> },
    { key: "contracts", label: "Contracts", width: 10, numeric: true, render: row => row.usage.contracts.toLocaleString() },
    { key: "active", label: "Active", width: 9, numeric: true, render: row => row.usage.activeContracts.toLocaleString() },
    { key: "state", label: "State", width: 11, render: row => <StatusBadge tone={templateStateTone(row.state)}>{templateStateLabel(row.state)}</StatusBadge> },
  ], []);

  const actions = admin ? <><button type="button" className="btn" onClick={() => void showKeywordRules()}>Keyword rules</button><button type="button" className="btn" disabled={busy} onClick={() => void proposeAgain()}>{busy ? "Working…" : "Propose again"}</button></> : undefined;
  return <><MigrationPage title="Contract templates" eyebrow="Service-contract templates in the latest extraction · approvals carry over to later unchanged extractions" sub="Each source template is split into a base warranty and reusable add-on cover blocks. Templates with the same blocks form one approval pattern." actions={actions}>
    {summary && <KpiStrip label="Contract template figures" kpis={[
      { label: "Templates", value: summary.templates.toLocaleString(), detail: `${summary.patterns.toLocaleString()} patterns · ${summary.activeContracts.toLocaleString()} active contracts` },
      { label: "Approved", value: summary.approved.templates.toLocaleString(), detail: `${summary.approved.patterns.toLocaleString()} patterns · ${summary.approved.activeContracts.toLocaleString()} active contracts` },
      { label: "Waiting for approval", value: summary.waitingForApproval.templates.toLocaleString(), detail: `${summary.waitingForApproval.patterns.toLocaleString()} patterns · ${summary.waitingForApproval.activeContracts.toLocaleString()} active contracts` },
      { label: "Needs a person", value: summary.needsPerson.templates.toLocaleString(), detail: `${summary.needsPerson.activeContracts.toLocaleString()} active contracts` },
      { label: "Policies to create", value: summary.policiesToCreate.toLocaleString(), detail: `${summary.waitingForPolicyPublication.activeContracts.toLocaleString()} active contracts wait for publication` },
    ]} />}
    <div className="note warn"><strong>Nothing is loaded from a template until it is approved.</strong>&nbsp;Contracts on an unapproved template keep their dates and go to the entitlement gap report at load time. The source has one duration with no unit: months are assumed and shown as such.</div>
    <ErrorNote error={error} />{notice && <div className="note" role="status">{notice}</div>}
    <section className="card migration-template-card" aria-labelledby="migration-template-patterns"><div className="card-header migration-template-header"><h2 id="migration-template-patterns">Patterns <span className="meta">{summary?.patterns.toLocaleString() ?? "—"} · most active contracts first</span></h2><div className="right"><div className="segmented migration-template-filters" role="group" aria-label="Template state">{[["", "All"], ...TEMPLATE_STATES.map(value => [value, templateStateLabel(value)] as const)].map(([value, label]) => <button type="button" key={value || "all"} aria-pressed={state === value} onClick={() => setState(value as TemplateMappingState | "")}>{label}</button>)}</div><label className="field migration-deleted-filter"><span className="sr-only">Deleted in source</span><select className="select" aria-label="Deleted in source" value={deleted} onChange={event => setDeleted(event.target.value as typeof deleted)}><option value="">All source templates</option><option value="false">Current in source</option><option value="true">Deleted in source</option></select></label></div></div>
      <DataGrid columns={patternColumns} page={{ rows: result?.items ?? [], total: result?.items.length ?? 0, page: pageNumber, pageSize: TAKE, continuation: result?.nextCursor ?? null, totalIsExact: false, hasMore: !!result?.nextCursor }} rowId={row => row.patternKey} onPage={go} onRowClick={row => setSelectedPatternKey(row.patternKey)} activeRowId={selected?.patternKey} loading={loading} viewportHeight={520} emptyText="No template patterns match these filters." footer={<><span className="muted">Patterns that need a person are never approved as a group. {TAKE} a page · keyset paged</span><span className="grow" /><div className="pager"><button type="button" className="btn sm" disabled={pageNumber === 1 || loading} onClick={() => go(pageNumber - 1)}>Previous</button><span className="pg">Page {pageNumber}</span><button type="button" className="btn sm" disabled={!result?.nextCursor || loading} onClick={() => go(pageNumber + 1)}>Next</button></div></>} />
    </section>
    <section className="card migration-template-card" aria-labelledby="migration-pattern-templates"><div className="card-header"><h2 id="migration-pattern-templates">{selected ? `Templates in “${selected.patternLabel}”` : "Templates in this pattern"} <span className="meta">{selected ? `${selected.templateCount.toLocaleString()} · click one for its terms and blocks` : "select a pattern"}</span></h2></div><DataGrid columns={templateColumns} page={{ rows: selected?.templates ?? [], total: selected?.templates.length ?? 0, page: 1, pageSize: Math.max(1, selected?.templates.length ?? 0), continuation: null }} rowId={row => row.templateKey} onPage={() => undefined} onRowClick={row => void showTemplate(row)} viewportHeight={420} emptyText="Select a pattern to see its templates." /></section>
  </MigrationPage><Rail label="Contract templates"><Section title="Approval gate"><p className="muted">An approved template with a generated draft policy remains outside the load until that policy version is published.</p></Section></Rail></>;
}

function TemplateDrawer({ initial, catalogue, onChanged }: { initial: TemplateMappingDetail; catalogue: PolicyCatalogue; onChanged: () => void }) {
  const [detail, setDetail] = useState(initial);
  const [blocks, setBlocks] = useState(initial.blocks.map(block => ({ familyCode: block.familyCode, policyId: block.resolution === "existing_policy" ? block.policyId : null })));
  const [busy, setBusy] = useState(false); const [error, setError] = useState<unknown>(null); const [notice, setNotice] = useState("");
  const editable = detail.state === "waiting" || detail.state === "needs_person";
  const activeFamilies = catalogue.families.filter(family => family.active);
  const save = async (approve: boolean) => {
    setBusy(true); setError(null); setNotice("");
    try {
      const saved = await migrationApi.updateTemplate(detail.templateKey, blocks, detail.version);
      const next = approve ? await migrationApi.approveTemplate(detail.templateKey) : saved;
      setDetail(next); setBlocks(next.blocks.map(block => ({ familyCode: block.familyCode, policyId: block.resolution === "existing_policy" ? block.policyId : null })));
      setNotice(approve ? templateStateLabel(next.state) : "Saved without approving."); onChanged();
    } catch (reason) { setError(reason); } finally { setBusy(false); }
  };
  const addBlock = () => {
    const family = activeFamilies.find(candidate => !blocks.some(block => block.familyCode === candidate.familyCode));
    if (family) setBlocks(current => [...current, { familyCode: family.familyCode, policyId: null }]);
  };
  return <>
    <div className="section"><div className="eyebrow">As stored in the source</div><dl className="kv wide"><dt className="k">Duration</dt><dd className="v">{detail.sourceTerms.durationValue === null ? "Not recorded" : `${detail.sourceTerms.durationValue.toLocaleString()} months`} {!detail.sourceTerms.durationUnitVerified && <span className="muted">(assumed: no unit in the source)</span>}</dd><dt className="k">Terms</dt><dd className="v">{templateTermsLabel(detail.sourceTerms)}</dd><dt className="k">Contract type</dt><dd className="v">{detail.sourceTerms.contractType ?? "Not recorded"}</dd><dt className="k">Used by</dt><dd className="v">{detail.usage.contracts.toLocaleString()} contracts · {detail.usage.activeContracts.toLocaleString()} active · {detail.usage.serials.toLocaleString()} serials</dd><dt className="k">State</dt><dd className="v"><StatusBadge tone={templateStateTone(detail.state)}>{templateStateLabel(detail.state)}</StatusBadge>{detail.deletedInSource && <span className="sub">Deleted in the source</span>}</dd></dl></div>
    <div className="section"><div className="eyebrow">Wording to read</div>{detail.wordingToReview.map((wording, index) => <p key={`${wording.kind}:${index}`}><span className="muted">{wording.kind === "staged_remark" ? "Source remark" : "Description"} · </span>{wording.text}</p>)}{detail.wordingToReview.length === 0 && <p className="muted">No description or remark was stored.</p>}<p className="muted migration-template-help">The source has no exclusions column. Remarks are shown here and are not read as cover.</p></div>
    <div className="section"><div className="eyebrow">Cover blocks</div>{editable ? blocks.map((block, index) => {
      const family = activeFamilies.find(item => item.familyCode === block.familyCode); const policies = catalogue.policies.filter(policy => policy.familyCode === block.familyCode);
      return <div className="migration-template-block" key={`${block.familyCode}:${index}`}><label className="field"><span className="lbl">Coverage family {index === 0 && <span className="muted">· one base family per template</span>}</span><select className="select" value={block.familyCode} onChange={event => setBlocks(current => current.map((item, itemIndex) => itemIndex === index ? { familyCode: event.target.value, policyId: null } : item))}>{activeFamilies.map(item => <option value={item.familyCode} key={item.familyCode}>{item.familyName}{item.stacking ? " · add-on" : " · base"}</option>)}</select></label><label className="field"><span className="lbl">Policy choice</span><select className="select" value={block.policyId ?? ""} onChange={event => setBlocks(current => current.map((item, itemIndex) => itemIndex === index ? { ...item, policyId: event.target.value || null } : item))}><option value="">Create a new policy from these terms</option>{policies.map(policy => <option value={policy.policyId} key={policy.policyId}>{policy.policyCode} v{policy.version} · {policy.policyName}</option>)}</select></label><button type="button" className="btn sm" disabled={blocks.length === 1} onClick={() => setBlocks(current => current.filter((_, itemIndex) => itemIndex !== index))}>Remove block</button>{family && <span className="sub">{family.stacking ? "Add-on cover" : "Base warranty"}</span>}</div>;
    }) : detail.blocks.map(block => <div className="migration-template-block readonly" key={block.familyCode}><strong>{templateFamilyLabel(block.familyCode, block.familyName)}</strong><span>{block.policyCode ? `${block.policyCode} v${block.policyVersion} · ${block.policyName}` : templateResolutionLabel(block.resolution)}</span>{block.policyStatus === "draft" && <StatusBadge tone="info">Draft policy</StatusBadge>}</div>)}{editable && <button type="button" className="btn sm" disabled={!activeFamilies.some(candidate => !blocks.some(block => block.familyCode === candidate.familyCode))} onClick={addBlock}>Add a block</button>}</div>
    {detail.reviewReason && <div className="note warn">{detail.reviewReason}</div>}<ErrorNote error={error} />{notice && <div className="note" role="status">{notice}</div>}
    {editable && <div className="section"><p className="muted migration-template-help">Changing a block moves this template out of its pattern into one of its own. Approving here approves this template only.</p><div className="migration-template-actions"><button type="button" className="btn primary" disabled={busy || blocks.length === 0} onClick={() => void save(true)}>{busy ? "Saving…" : "Approve this template"}</button><button type="button" className="btn" disabled={busy || blocks.length === 0} onClick={() => void save(false)}>Save without approving</button></div></div>}
  </>;
}

function KeywordRulesForm({ initial, catalogue, onSaved }: { initial: TemplateKeywordRules; catalogue: PolicyCatalogue; onSaved: () => void }) {
  const [rules, setRules] = useState<TemplateKeywordRule[]>(initial.rules); const [version, setVersion] = useState(initial.version);
  const [busy, setBusy] = useState(false); const [error, setError] = useState<unknown>(null); const [notice, setNotice] = useState("");
  const families = catalogue.families.filter(family => family.active);
  const save = async () => { setBusy(true); setError(null); setNotice(""); try { const saved = await migrationApi.saveTemplateKeywordRules(rules, version); setRules(saved.rules); setVersion(saved.version); setNotice("Keyword rules saved. Choose Propose again to apply them to draft templates."); onSaved(); } catch (reason) { setError(reason); } finally { setBusy(false); } };
  return <div className="section"><p className="muted">When source wording contains a keyword, its coverage family is proposed as another block. Rules are a tenant setting.</p>{rules.map((rule, index) => <div className="migration-keyword-rule" key={index}><label className="field"><span className="lbl">Keyword</span><input className="input" value={rule.keyword} onChange={event => setRules(current => current.map((item, itemIndex) => itemIndex === index ? { ...item, keyword: event.target.value } : item))} /></label><label className="field"><span className="lbl">Coverage family</span><select className="select" value={rule.familyCode} onChange={event => setRules(current => current.map((item, itemIndex) => itemIndex === index ? { ...item, familyCode: event.target.value } : item))}>{families.map(family => <option value={family.familyCode} key={family.familyCode}>{family.familyName}</option>)}</select></label><button type="button" className="btn sm" disabled={rules.length === 1} onClick={() => setRules(current => current.filter((_, itemIndex) => itemIndex !== index))}>Remove</button></div>)}<button type="button" className="btn sm" onClick={() => setRules(current => [...current, { keyword: "", familyCode: families[0]?.familyCode ?? "" }])}>Add keyword</button><ErrorNote error={error} />{notice && <div className="note" role="status">{notice}</div>}<div className="migration-template-actions"><button type="button" className="btn primary" disabled={busy || rules.some(rule => !rule.keyword.trim() || !rule.familyCode)} onClick={() => void save()}>{busy ? "Saving…" : "Save keyword rules"}</button></div></div>;
}
