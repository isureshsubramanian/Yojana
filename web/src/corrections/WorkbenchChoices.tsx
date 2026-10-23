import { useEffect, useMemo, useState } from "react";
import { Link } from "react-router";
import { DataGrid, KpiStrip, StatusBadge, type GridColumn } from "../components";
import { usePeople } from "../people/people";
import { Rail } from "../shell/Drawer";
import { Icon } from "../shell/Icons";
import { openDrawer } from "../shell/drawerStore";
import { Breadcrumbs } from "../shell/PageHeader";
import { ErrorNote, Kv, Section } from "../units/common";
import { fmtDateTime, humanise } from "../units/format";
import { correctionsApi } from "./api";
import { ROOT_CAUSES, slugChoice } from "./format";
import type { CorrectionKpiReport, WorkbenchChoice, WorkbenchChoiceConfiguration, WorkbenchChoiceSettingsView } from "./types";

const COMMANDS: Record<string, { label: string; root: number; reason: string; commitRoles: string[] }> = {
  correct_document_serials: { label: "Correct document serials", root: 1, reason: "shipped_wrong_units", commitRoles: ["shipping_supervisor"] },
  correct_identifier: { label: "Correct an identifier", root: 4, reason: "typo", commitRoles: ["serial_administrator"] },
  swap_identifiers: { label: "Swap identifiers", root: 4, reason: "cross_recorded_at_build", commitRoles: ["serial_administrator"] },
  correct_item: { label: "Correct the item", root: 5, reason: "wrong_item_at_build", commitRoles: ["inventory_controller", "service_supervisor"] },
  correct_source_document: { label: "Correct the source document", root: 5, reason: "released_under_wrong_order", commitRoles: ["order_desk_supervisor"] },
  reassign_allocation: { label: "Reassign an allocation", root: 6, reason: "allocation_error", commitRoles: ["production_supervisor"] },
  void_unit: { label: "Void a unit", root: 6, reason: "work_order_quantity_reduced", commitRoles: ["production_supervisor"] },
  merge_units: { label: "Merge units", root: 4, reason: "duplicate_record", commitRoles: ["serial_administrator"] },
  correct_as_built: { label: "Correct as-built parts", root: 5, reason: "component_not_captured", commitRoles: ["production_supervisor", "quality"] },
  assign_identifiers: { label: "Assign identifiers", root: 6, reason: "vendor_list", commitRoles: ["production_supervisor"] },
  late_serialization: { label: "Serialise an item late", root: 6, reason: "coding_error_at_item_setup", commitRoles: ["item_master_owner"] },
  correct_replacement_link: { label: "Correct a replacement link", root: 2, reason: "wrong_unit_recorded", commitRoles: ["service_manager"] },
  "entitlement.amend": { label: "Amend an entitlement", root: 7, reason: "correction", commitRoles: ["service_manager"] },
};

function iso(date: Date): string { return date.toISOString().slice(0, 10); }

export function Component() {
  const [view, setView] = useState<WorkbenchChoiceSettingsView | null>(null);
  const [kpi, setKpi] = useState<CorrectionKpiReport | null>(null);
  const [filter, setFilter] = useState("all");
  const [active, setActive] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<unknown>(null);
  const updatedBy = view?.current.updatedBy;
  const person = usePeople([updatedBy]);

  const load = async () => {
    setLoading(true); setError(null);
    const end = new Date(); const start = new Date(end); start.setUTCDate(start.getUTCDate() - 29);
    try { const [settings, report] = await Promise.all([correctionsApi.workbenchChoices(), correctionsApi.kpi(iso(start), iso(end), "root_cause")]); setView(settings); setKpi(report); } catch (reason) { setError(reason); } finally { setLoading(false); }
  };
  useEffect(() => { void load(); }, []);
  const choices = view?.current.choices ?? [];
  const filtered = choices.filter((choice) => filter === "all" || (filter === "standard" && choice.standard) || (filter === "added" && !choice.standard) || (filter === "retired" && choice.status === "retired"));
  const shown = choices.filter((choice) => choice.status === "shown");
  const standard = choices.filter((choice) => choice.standard && !choice.fallback).length;
  const added = choices.filter((choice) => !choice.standard).length;
  const retired = choices.filter((choice) => choice.status === "retired").length;

  const saveConfiguration = async (choicesToSave: WorkbenchChoice[], threshold = view?.current.unclassifiedAlertPercent ?? 10): Promise<WorkbenchChoiceConfiguration> => {
    const saved = await correctionsApi.saveWorkbenchChoices({ choices: choicesToSave, unclassifiedAlertPercent: threshold });
    setView((current) => current ? { ...current, current: saved } : { current: saved, history: [] });
    return saved;
  };
  const saveChoice = async (draft: WorkbenchChoice) => {
    const id = draft.choice || slugChoice(draft.label, choices);
    const saved = { ...draft, choice: id };
    const exists = choices.some((choice) => choice.choice === id);
    const next = exists ? choices.map((choice) => choice.choice === id ? saved : choice) : [...choices, saved];
    await saveConfiguration(next);
  };
  const openChoice = (choice: WorkbenchChoice) => { setActive(choice.choice); openDrawer({ eyebrow: `Choice · ${choice.standard ? "standard" : "added by administrator"}`, title: choice.label }, <ChoiceEditor choice={choice} initiatorRoles={choices.find((row) => !row.fallback)?.initiateRoles ?? []} onSave={saveChoice} />); };
  const addChoice = () => {
    const firstCommand = Object.keys(COMMANDS)[0]!; const meta = COMMANDS[firstCommand]!;
    const choice: WorkbenchChoice = { choice: "", label: "", area: "Documents and orders", helpText: "", commands: [firstCommand], questions: [], defaultReason: meta.reason, rootCause: meta.root, searchWords: [], order: Math.max(0, ...choices.filter((row) => !row.fallback).map((row) => row.order)) + 10, initiateRoles: choices.find((row) => !row.fallback)?.initiateRoles ?? [], commitRoles: meta.commitRoles, status: "shown", standard: false, fallback: false };
    setActive(null); openDrawer({ eyebrow: "Choice · new", title: "Add workbench choice" }, <ChoiceEditor choice={choice} initiatorRoles={choice.initiateRoles ?? []} onSave={saveChoice} />);
  };
  const columns: GridColumn<WorkbenchChoice>[] = [
    { key: "choice", label: "Choice · as the user reads it", width: 30, render: (row) => <span className="primary correction-choice-help"><span className="t">{row.label}</span><span className="sub">{row.fallback ? "standard · always last · cannot be retired" : row.standard ? "standard" : "added by administrator"}</span></span> },
    { key: "area", label: "Area", width: 14, render: (row) => row.fallback ? "always last" : row.area },
    { key: "command", label: "Runs command", width: 20, render: (row) => row.fallback ? <span className="muted">correction request</span> : row.commands.map((command) => COMMANDS[command]?.label ?? humanise(command)).join(" · ") },
    { key: "root", label: "Root cause", width: 10, render: (row) => row.fallback ? <span className="muted">unclassified</span> : `${row.rootCause} · ${ROOT_CAUSES[String(row.rootCause)]?.label ?? ""}` },
    { key: "roles", label: "Commit without approver", width: 14, render: (row) => row.fallback ? <span className="muted">serial administrator re-files</span> : (row.commitRoles ?? []).map(humanise).join(" + ") },
    { key: "status", label: "Status · use 30 d", width: 12, render: (row) => <><StatusBadge tone={row.status === "shown" ? "ok" : "neutral"}>{row.status === "shown" ? "Shown" : "Retired"}</StatusBadge><span className="sub">usage feed unavailable</span></> },
  ];
  const alert = kpi?.unclassifiedAlert;

  return <>
    <main className="content" id="main" tabIndex={-1}>
      <Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Settings", to: "/settings" }, { label: "Workbench choices" }]} />
      <div className="page-header"><div><div className="eyebrow">Settings · application administrator</div><h1>Workbench choices</h1><p className="sub">The “what is wrong” list users see in the correction workbench. Add choices in your own words; every choice runs one of the product’s audited correction commands, so a new choice never edits a field directly.</p></div><div className="actions"><Link className="btn" to="/corrections">Preview as a user</Link><button type="button" className="btn primary" onClick={addChoice}><Icon name="plus" />Add choice</button></div></div>
      <ErrorNote error={error} />
      <div className="stack">
        <KpiStrip label="Workbench choice figures" kpis={[
          { label: "Choices shown", value: shown.length.toLocaleString(), detail: `${standard} standard · ${added} added`, trend: null },
          { label: "Unclassified requests · 30 d", value: (alert?.unclassifiedRequests ?? 0).toLocaleString(), detail: `${alert?.ratePercent.toFixed(1) ?? "0.0"} % of requests · alert above ${view?.current.unclassifiedAlertPercent.toFixed(1) ?? "—"} %`, trend: alert?.alert ? "down" : null },
          { label: "Most re-filed to", value: "—", detail: "classification analytics are not in settings", trend: null },
          { label: "No command fits", value: "—", detail: "request-status analytics are not exposed", trend: null },
        ]} />
        <section className="card" aria-labelledby="choice-list"><div className="card-header"><h2 id="choice-list">Choices <span className="meta">in the order users see them</span></h2><div className="segmented plain" role="group" aria-label="Filter">{[["all", `All ${choices.length}`], ["standard", `Standard ${standard}`], ["added", `Added ${added}`], ["retired", `Retired ${retired}`]].map(([value, label]) => <button key={value} type="button" aria-pressed={filter === value} onClick={() => setFilter(value!)}>{label}</button>)}</div></div>
          <DataGrid dense columns={columns} page={{ rows: filtered.slice().sort((a, b) => (a.fallback ? Number.MAX_SAFE_INTEGER : a.order) - (b.fallback ? Number.MAX_SAFE_INTEGER : b.order)), total: filtered.length, page: 1, pageSize: Math.max(1, filtered.length), continuation: null }} rowId={(row) => row.choice} onPage={() => undefined} onRowClick={openChoice} activeRowId={active} loading={loading} emptyText="No choices match this filter." viewportHeight={560} footerNote="Standard choices can be re-worded and re-ordered but not retired; added choices can be retired, never deleted" />
        </section>
      </div>
    </main>
    <Rail label="Workbench choice settings">{view && <Section title="Unclassified alert"><ThresholdEditor value={view.current.unclassifiedAlertPercent} choices={choices} onSave={saveConfiguration} /></Section>}<Section title="Last change">{view && view.current.version === 0 ? <p className="muted">Product defaults; nobody has changed these choices yet.</p> : <Kv rows={[["Version", view?.current.version ?? "—"], ["Changed by", person(updatedBy)], ["Changed at", fmtDateTime(view?.current.updatedAt)], ["Earlier versions", view?.history.length ?? "—"]]} />}</Section></Rail>
  </>;
}

function ChoiceEditor({ choice, initiatorRoles, onSave }: { choice: WorkbenchChoice; initiatorRoles: string[] | null; onSave: (choice: WorkbenchChoice) => Promise<void> }) {
  const [draft, setDraft] = useState(choice);
  const [searchWords, setSearchWords] = useState((choice.searchWords ?? []).join(", "));
  const [commitRoles, setCommitRoles] = useState((choice.commitRoles ?? []).join(", "));
  const [busy, setBusy] = useState(false);
  const [saved, setSaved] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const isNew = !choice.choice;
  const command = draft.commands[0] ?? "";
  const updateCommand = (value: string) => { const meta = COMMANDS[value]!; setDraft({ ...draft, commands: [value], questions: [], defaultReason: meta.reason, rootCause: meta.root, commitRoles: meta.commitRoles }); setCommitRoles(meta.commitRoles.join(", ")); };
  const save = async (status = draft.status) => { setBusy(true); setSaved(false); setError(null); try { await onSave({ ...draft, status, searchWords: words(searchWords), initiateRoles: draft.initiateRoles ?? initiatorRoles ?? [], commitRoles: words(commitRoles) }); setDraft({ ...draft, status }); setSaved(true); } catch (reason) { setError(reason); } finally { setBusy(false); } };
  return <><h3>What the user reads</h3><div className="form-grid"><div className="field span"><label htmlFor="choice-label">Label</label><input id="choice-label" className="input" value={draft.label} disabled={draft.fallback} onChange={(event) => setDraft({ ...draft, label: event.target.value })} /></div><div className="field span"><label htmlFor="choice-area">Area · groups the list</label><input id="choice-area" className="input" value={draft.area} onChange={(event) => setDraft({ ...draft, area: event.target.value })} /></div><div className="field span"><label htmlFor="choice-search">Search words</label><input id="choice-search" className="input" value={searchWords} onChange={(event) => setSearchWords(event.target.value)} /></div><div className="field span"><label htmlFor="choice-help">Help text</label><textarea id="choice-help" className="textarea" value={draft.helpText} onChange={(event) => setDraft({ ...draft, helpText: event.target.value })} /></div></div><h3 style={{ marginTop: 20 }}>What it runs</h3><div className="form-grid"><div className="field span"><label htmlFor="choice-command">Command · from the product’s list</label><select id="choice-command" className="select" value={command} disabled={!isNew || draft.fallback} onChange={(event) => updateCommand(event.target.value)}>{draft.fallback && <option value="">Correction request</option>}{Object.entries(COMMANDS).map(([value, meta]) => <option key={value} value={value}>{meta.label}</option>)}</select></div><div className="field span"><label htmlFor="choice-reason">Default reason</label><input id="choice-reason" className="input serial" value={draft.defaultReason} disabled={!isNew || draft.fallback} onChange={(event) => setDraft({ ...draft, defaultReason: event.target.value })} /></div><div className="field"><label htmlFor="choice-root">Root cause in the KPI</label><select id="choice-root" className="select" value={draft.rootCause} disabled={!isNew || draft.fallback} onChange={(event) => setDraft({ ...draft, rootCause: Number(event.target.value) })}>{draft.fallback && <option value="0">Unclassified</option>}{Array.from({ length: 7 }, (_, index) => index + 1).map((root) => <option key={root} value={root}>{root} · {ROOT_CAUSES[String(root)]?.label}</option>)}</select></div><div className="field"><label htmlFor="choice-order">Display order</label><input id="choice-order" className="input" type="number" value={draft.order} disabled={draft.fallback} onChange={(event) => setDraft({ ...draft, order: Number(event.target.value) })} /></div><div className="field span"><label htmlFor="choice-roles">May commit without an approver · roles</label><input id="choice-roles" className="input" value={commitRoles} disabled={draft.fallback} onChange={(event) => setCommitRoles(event.target.value)} /></div><div className="span note">The command’s own rules still apply: approvals can be made stricter here, never weaker than the command requires. Existing audited command routes cannot be changed; retire an added choice and create another.</div></div><ErrorNote error={error} />{saved && <div className="note ok"><strong>Choice saved.</strong>&nbsp;The workbench sees the new configuration immediately.</div>}<div className="act"><button type="button" className="btn primary block" disabled={busy || !draft.label.trim() || !draft.area.trim() || !draft.helpText.trim()} onClick={() => void save()}>Save choice</button>{!draft.standard && draft.status !== "retired" && <button type="button" className="btn block" disabled={busy} onClick={() => void save("retired")}>Retire choice</button>}</div></>;
}

function ThresholdEditor({ value, choices, onSave }: { value: number; choices: WorkbenchChoice[]; onSave: (choices: WorkbenchChoice[], threshold?: number) => Promise<WorkbenchChoiceConfiguration> }) {
  const [threshold, setThreshold] = useState(String(value)); const [busy, setBusy] = useState(false); const [error, setError] = useState<unknown>(null);
  const save = async () => { setBusy(true); setError(null); try { await onSave(choices, Number(threshold)); } catch (reason) { setError(reason); } finally { setBusy(false); } };
  return <div className="field"><label htmlFor="choice-threshold">Alert above · percent</label><input id="choice-threshold" className="input" type="number" min="0" max="100" step="0.1" value={threshold} onChange={(event) => setThreshold(event.target.value)} /><span className="help">Warn when more than this share of correction requests are filed as "None of these".</span><ErrorNote error={error} /><button type="button" className="btn sm" style={{ alignSelf: "flex-start" }} disabled={busy || Number(threshold) < 0 || Number(threshold) > 100} onClick={() => void save()}>Save threshold</button></div>;
}
function words(value: string): string[] { return value.split(",").map((word) => word.trim()).filter(Boolean); }
