import { useEffect, useMemo, useState } from "react";
import { Link, useSearchParams } from "react-router";
import { DEFAULT_LEGAL_ENTITY, newCommandId, now, type HistoryRow } from "../api/units";
import { StatusBadge } from "../components";
import { openDrawer } from "../shell/drawerStore";
import { Rail } from "../shell/Drawer";
import { Icon } from "../shell/Icons";
import { Breadcrumbs } from "../shell/PageHeader";
import { usePeople } from "../people/people";
import { ErrorNote, Kv, Section } from "../units/common";
import { documentLabel, fmtDate, fmtDateTime, humanise, outcomeTone, shortId, statusLabel, statusTone } from "../units/format";
import {
  DEDICATED_SCREENS, commandName, recordedRows, workbenchApi,
  type WorkbenchCandidate, type WorkbenchChoice, type WorkbenchChoiceOption, type WorkbenchEvidenceRef, type WorkbenchPicker,
  type WorkbenchPreviewRequest, type WorkbenchPreviewResult, type WorkbenchRequestRow, type WorkbenchUnitLineage,
} from "./workbenchApi";

const ENTRY_KINDS = [["identifier", "Serial or tag"], ["document_line", "Document line"], ["selection_set", "Selection set"]] as const;
const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
/** Commands whose arguments this screen collects itself; the rest hand over to their own screen or take raw arguments. */
const INLINE = ["correct_identifier", "swap_identifiers", "void_unit", "correct_item"];

/** P3-T19: the serial correction workbench. Pick what is wrong; every path ends in one existing, audited command. */
export function Component() {
  const [params] = useSearchParams();
  const [entryKind, setEntryKind] = useState<string>("identifier");
  const [entryValue, setEntryValue] = useState(params.get("q") ?? "");
  const [documentType, setDocumentType] = useState("delivery");
  const [candidates, setCandidates] = useState<WorkbenchCandidate[]>([]);
  const [unitId, setUnitId] = useState<string | null>(params.get("unit"));
  const [lineage, setLineage] = useState<WorkbenchUnitLineage | null>(null);
  const [search, setSearch] = useState("");
  const [picker, setPicker] = useState<WorkbenchPicker | null>(null);
  const [choiceId, setChoiceId] = useState<string | null>(null);
  const [option, setOption] = useState<WorkbenchChoiceOption | null>(null);
  const [args, setArgs] = useState<Record<string, string>>({});
  const [rawArgs, setRawArgs] = useState("{}");
  const [reasons, setReasons] = useState<string[]>([]);
  const [reason, setReason] = useState("");
  const [corrects, setCorrects] = useState("");
  const [note, setNote] = useState("");
  const [evidence, setEvidence] = useState<WorkbenchEvidenceRef[]>([]);
  const [evidenceRef, setEvidenceRef] = useState("");
  const [description, setDescription] = useState("");
  const [preview, setPreview] = useState<WorkbenchPreviewResult | null>(null);
  const [previewing, setPreviewing] = useState(false);
  const [previewError, setPreviewError] = useState<unknown>(null);
  const [request, setRequest] = useState<WorkbenchRequestRow | null>(null);
  const [commandId, setCommandId] = useState(newCommandId);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const person = usePeople([...(lineage?.lastEvents.map((e) => e.actorRef) ?? []), request?.initiatedBy]);

  const resolve = async (value = entryValue) => {
    if (!value.trim()) return;
    setBusy(true); setError(null); setCandidates([]); reset();
    try {
      const entry = entryKind === "document_line"
        ? { kind: entryKind, document: { type: documentType, id: value.trim() } }
        : entryKind === "selection_set" ? { kind: entryKind, selectionId: value.trim() } : { kind: entryKind, value: value.trim() };
      const result = await workbenchApi.resolve(entry);
      setCandidates(result.candidates);
      setUnitId(result.candidates.length === 1 ? result.candidates[0]!.resolvedUnitId : null);
      if (result.candidates.length === 0) setError(new Error(`Nothing resolves from "${value.trim()}". Superseded serials, asset tags and licence ids are accepted.`));
    } catch (reason) { setError(reason); } finally { setBusy(false); }
  };
  useEffect(() => { if (entryValue && !unitId) void resolve(); }, []); // eslint-disable-line react-hooks/exhaustive-deps

  useEffect(() => {
    if (!unitId) { setLineage(null); return; }
    const controller = new AbortController();
    workbenchApi.lineage(unitId, controller.signal).then(setLineage).catch((reason) => { if (!aborted(reason)) setError(reason); });
    return () => controller.abort();
  }, [unitId]);

  useEffect(() => {
    const controller = new AbortController();
    const timer = window.setTimeout(() => workbenchApi.picker(unitId, search, controller.signal).then(setPicker).catch((reason) => { if (!aborted(reason)) setError(reason); }), 200);
    return () => { window.clearTimeout(timer); controller.abort(); };
  }, [unitId, search]);

  const choices = useMemo(() => picker ? [...picker.groups.flatMap((g) => g.choices), picker.fallback] : [], [picker]);
  const choice: WorkbenchChoice | null = choices.find((c) => c.choice === choiceId) ?? null;
  const options = choice ? (choice.questions[0]?.options ?? [{ value: "only", label: choice.label, command: choice.commands[0] ?? "", defaultReason: choice.defaultReason, rootCause: choice.rootCause }]) : [];
  const command = option?.command ?? null;

  // ?choice=<id>&route=<value> opens the workbench at a choice (links from the unit page, the approver view and the KPI).
  const [linked, setLinked] = useState(params.get("choice"));
  useEffect(() => {
    const target = linked ? choices.find((c) => c.choice === linked) : null;
    if (!target) return;
    pick(target);
    const route = params.get("route");
    const wanted = target.questions[0]?.options.find((o) => o.value === route || o.command === route);
    if (wanted) setOption(wanted);
    setLinked(null);
  }, [choices, linked]); // eslint-disable-line react-hooks/exhaustive-deps

  function reset() { setChoiceId(null); setOption(null); setPreview(null); setRequest(null); setArgs({}); setRawArgs("{}"); setCommandId(newCommandId()); }
  const pick = (next: WorkbenchChoice) => {
    setChoiceId(next.choice); setPreview(null); setRequest(null); setArgs({}); setRawArgs("{}"); setCommandId(newCommandId());
    const only = next.questions.length === 0 && next.commands[0] ? { value: "only", label: next.label, command: next.commands[0], defaultReason: next.defaultReason, rootCause: next.rootCause } : null;
    setOption(only);
  };

  useEffect(() => {
    if (!command) { setReasons([]); return; }
    const controller = new AbortController();
    workbenchApi.reasons(command, controller.signal).then((list) => { setReasons(list); setReason(option?.defaultReason && list.includes(option.defaultReason) ? option.defaultReason : list[0] ?? ""); }).catch((reason) => { if (!aborted(reason)) setError(reason); });
    return () => controller.abort();
  }, [command, option?.defaultReason]);

  // Arguments travel as the command's own JSON in the ledger's snake_case (WorkbenchCommandDispatcher reads them with
  // LedgerJson.Options); identity and approval fields are overwritten by the server.
  const commandArguments = useMemo<Record<string, unknown> | null>(() => {
    if (!command || !unitId) return null;
    const base: Record<string, unknown> = { event_time: now(), actor_ref: "", legal_entity_id: DEFAULT_LEGAL_ENTITY, unit_id: unitId };
    if (corrects) base.correction = { corrects_correction_id: corrects };
    switch (command) {
      case "correct_identifier": return args.type && args.value?.trim() ? { ...base, identifier_type: args.type, new_raw_value: args.value.trim() } : null;
      case "swap_identifiers": { const { unit_id: _, ...rest } = base; return args.type && args.other ? { ...rest, first_unit_id: unitId, second_unit_id: args.other, identifier_type: args.type } : null; }
      case "void_unit": return { ...base, production_order: GUID.test(args.order ?? "") ? { type: "production_order", id: args.order } : null };
      case "correct_item": return GUID.test(args.item ?? "") ? { ...base, corrected_item_id: args.item } : null;
      default: if (DEDICATED_SCREENS[command]) return null; try { return { ...(JSON.parse(rawArgs) as Record<string, unknown>), ...(corrects ? { correction: base.correction } : {}) }; } catch { return null; }
    }
  }, [command, unitId, args, rawArgs, corrects]);

  const previewRequest = useMemo<WorkbenchPreviewRequest | null>(() => choice && command && commandArguments && reason && unitId ? {
    commandId, choice: choice.choice, reason, note: note || null, evidence,
    answers: { command, arguments: commandArguments, narrowing: option && choice.questions[0] ? { [choice.questions[0].key]: option.value } : null },
    target: { kind: "unit", unitId },
  } : null, [choice, command, commandArguments, reason, unitId, commandId, note, evidence, option]);
  const previewKey = useMemo(() => previewRequest ? JSON.stringify({ ...previewRequest, answers: { ...previewRequest.answers, arguments: { ...(previewRequest.answers.arguments as object), eventTime: undefined } } }) : null, [previewRequest]);

  useEffect(() => {
    if (!previewRequest || request) return;
    const controller = new AbortController();
    const timer = window.setTimeout(() => {
      setPreviewing(true); setPreviewError(null);
      workbenchApi.preview(previewRequest, controller.signal).then((result) => { setPreview(result); setPreviewing(false); })
        .catch((reason) => { if (!aborted(reason)) { setPreview(null); setPreviewError(reason); setPreviewing(false); } });
    }, 350);
    return () => { window.clearTimeout(timer); controller.abort(); };
  }, [previewKey]); // eslint-disable-line react-hooks/exhaustive-deps

  const submit = async () => {
    setBusy(true); setError(null);
    try {
      if (choice?.fallback) {
        if (!unitId) return;
        setRequest(await workbenchApi.fileUnclassified(newCommandId(), { kind: "unit", unitId }, description, evidence));
      } else if (previewRequest) {
        setRequest(await workbenchApi.initiate(newCommandId(), previewRequest));
      }
    } catch (reason) { setError(reason); } finally { setBusy(false); }
  };
  const commit = async () => {
    if (!request) return;
    setBusy(true); setError(null);
    try { setRequest(await workbenchApi.commit(request.requestId)); } catch (reason) { setError(reason); } finally { setBusy(false); }
  };

  const state = lineage?.state;
  const owner = lineage?.episodes.find((e) => e.kind === "ownership" && e.validTo === null);
  const custody = lineage?.episodes.find((e) => e.kind === "custody" && e.validTo === null);
  const live = lineage?.entitlements.filter((e) => e.status === "live" || e.status === "active") ?? [];
  const primary = lineage?.identifiers.find((i) => i.validTo === null && i.type === "internal_serial") ?? lineage?.identifiers.find((i) => i.validTo === null);
  const openTypes = [...new Set(lineage?.identifiers.filter((i) => i.validTo === null).map((i) => i.type) ?? [])];
  const blocked = !!preview && (preview.preview.block > 0 || !!preview.preview.refusal);
  const ready = choice?.fallback ? !!unitId && description.trim().length > 0 : !!preview && !blocked && !previewing;
  const choiceCount = choices.length;

  const showEvent = (event: HistoryRow) => {
    let attributes = event.attributes;
    try { attributes = JSON.stringify(JSON.parse(event.attributes ?? "{}"), null, 2); } catch { /* raw text */ }
    openDrawer({ eyebrow: `Event · ${primary?.rawValue ?? ""}`, title: event.eventType, mono: true }, <>
      <h3>Event</h3>
      <Kv rows={[["Effective", fmtDateTime(event.eventTime)], ["Recorded", fmtDateTime(event.recordedTime)], ["Actor", person(event.actorRef)], ["Source document", event.document ? documentLabel(event.document) : "—"], ["Event id", shortId(event.eventId, 12)]]} />
      <h3 style={{ marginTop: 16 }}>Is this event the problem?</h3>
      <p className="muted">A wrong event is never edited. Pick what is wrong on the right; the command records a new event that references this one.</p>
      <pre className="raw" style={{ whiteSpace: "pre-wrap", overflow: "auto", maxHeight: 280 }}>{attributes ?? "{}"}</pre>
    </>);
  };

  return <>
    <main className="content" id="main" tabIndex={-1}>
      <Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Correction workbench" }]} />
      <div className="page-header">
        <div>
          <div className="eyebrow">Something is wrong with this serial · every path ends in one audited command</div>
          <h1>Correction workbench</h1>
          <p className="sub">No field is edited here. Pick what is wrong; the workbench shows the command's own preview, collects reason and evidence, and routes to an approver where the tenant's policy requires one.</p>
        </div>
        <div className="actions">
          <button type="button" className="btn ghost" onClick={() => { reset(); setDescription(""); setNote(""); setEvidence([]); }}>Discard</button>
          {request?.status === "approved"
            ? <button type="button" className="btn primary" disabled={busy} aria-busy={busy} onClick={commit}>Commit<kbd className="key">⌘↵</kbd></button>
            : <button type="button" className="btn primary" disabled={!ready || busy || !!request} aria-busy={busy} onClick={submit}>{choice?.fallback ? "File request" : "Submit for approval"}<kbd className="key">⌘↵</kbd></button>}
        </div>
      </div>

      <section className="card" style={{ marginBottom: "var(--gap)" }}>
        <div className="card-body" style={{ display: "flex", gap: 12, alignItems: "center", flexWrap: "wrap" }}>
          {entryKind === "document_line" && <input className="input" style={{ width: 140 }} aria-label="Document type" value={documentType} onChange={(event) => setDocumentType(event.target.value)} />}
          <form className="scanbox lg" style={{ flex: "1 1 320px", maxWidth: 460, margin: 0 }} onSubmit={(event) => { event.preventDefault(); void resolve(); }}>
            <Icon name="scan" />
            <input className="input" aria-label="Serial, tag, document or selection" placeholder={entryKind === "identifier" ? "Scan or type a serial, asset tag or licence id" : entryKind === "document_line" ? "Document id" : "Selection set id"} value={entryValue} onChange={(event) => setEntryValue(event.target.value)} />
            <span className="scan-hint"><kbd className="key">F2</kbd></span>
          </form>
          <div className="segmented plain" role="group" aria-label="Entry">{ENTRY_KINDS.map(([id, label]) => <button key={id} type="button" aria-pressed={entryKind === id} onClick={() => setEntryKind(id)}>{label}</button>)}</div>
          <span className="hint" style={{ marginLeft: "auto" }}>Accepts superseded serials, asset tags and licence ids.{primary ? ` Resolved: ${humanise(primary.type)}, current.` : ""}</span>
        </div>
        {candidates.length > 1 && <div className="card-body" style={{ paddingTop: 0 }}>
          <div className="note info"><strong>{candidates.length} units match.</strong>&nbsp;Pick one:&nbsp;{candidates.map((c) => <button key={c.unitId} type="button" className={`chip${unitId === c.resolvedUnitId ? " on" : ""}`} onClick={() => setUnitId(c.resolvedUnitId)}>{c.displayIdentifier ?? shortId(c.unitId)}{c.mergedAlias ? " · merged alias" : ""}</button>)}</div>
        </div>}
      </section>
      <ErrorNote error={error} />

      <div className="cols-2-3 top">
        <div className="stack">
          <section className="card" aria-labelledby="wb-unit">
            <div className="card-header"><h2 id="wb-unit">Unit <span className="serial" style={{ fontSize: 13 }}>{primary?.rawValue ?? (unitId ? shortId(unitId) : "—")}</span></h2>{state && <StatusBadge tone={statusTone(state.lifecycleStatus)}>{statusLabel(state.lifecycleStatus)}</StatusBadge>}</div>
            <div className="card-body">{state ? <Kv rows={[
              ["Item", shortId(state.itemId, 12)],
              ["Owner", owner ? `${humanise(owner.partyType)} ${shortId(owner.partyRef)} · since ${fmtDate(owner.validFrom)}` : "no owner recorded"],
              ["Opened by", owner?.openingDocument ? documentLabel(owner.openingDocument) : "—"],
              ["Custody", custody ? `${humanise(custody.locationKind ?? custody.partyType)}${custody.partyRef ? ` · ${shortId(custody.partyRef)}` : ""}` : "—"],
              ["Entitlements", live.length ? live.map((e) => `${e.coverageFamily}${e.endDate ? ` to ${e.endDate}` : ""}`).join(" · ") : "none live"],
              ["Earlier corrections", lineage?.corrections.length ? lineage.corrections.map((c) => humanise(c.command)).join(" · ") : "none"],
              ["Merged into", state.mergedIntoUnitId ? shortId(state.mergedIntoUnitId, 12) : "—"],
            ]} /> : <p className="muted">Scan or type a serial to see the unit, its identifier lineage and its last events.</p>}</div>
          </section>

          <section className="card" aria-labelledby="wb-lineage">
            <div className="card-header"><h2 id="wb-lineage">Identifier lineage</h2><span className="meta">{lineage?.identifiers.length ?? 0} rows</span></div>
            <table className="table dense"><thead><tr><th>Value</th><th>Type</th><th>Valid</th><th>Link</th></tr></thead><tbody>
              {(lineage?.identifiers ?? []).map((i) => <tr key={i.identifierId}><td className="serial">{i.rawValue}</td><td>{i.type}</td><td>{i.validTo ? `to ${fmtDate(i.validTo)}` : `from ${fmtDate(i.validFrom)}`}</td><td>{i.validTo ? <>superseded<span className="sub">{i.correctionReason ? humanise(i.correctionReason) : ""}</span></> : "current"}</td></tr>)}
              {!lineage && <tr><td colSpan={4} className="empty-cell">No unit selected.</td></tr>}
            </tbody></table>
          </section>

          <section className="card" aria-labelledby="wb-events">
            <div className="card-header"><h2 id="wb-events">Last ten events</h2>{unitId && <Link className="btn ghost" to={`/units/${unitId}`}>Full history</Link>}</div>
            <div className="card-body"><ol className="timeline">{(lineage?.lastEvents ?? []).map((e, index) => <li key={e.eventId} className={`ev${index === 0 ? " key" : ""}`} style={{ cursor: "pointer" }} onClick={() => showEvent(e)}><span className="t">{e.eventType}</span><span className="m">{fmtDateTime(e.eventTime)} · {person(e.actorRef)}{e.document ? ` · ${documentLabel(e.document)}` : ""}</span></li>)}</ol>{!lineage && <p className="muted">—</p>}</div>
          </section>
        </div>

        <div className="stack">
          <section className="card" aria-labelledby="wb-what">
            <div className="card-header"><h2 id="wb-what">What is wrong?</h2><span className="meta">Step 1 of 3 · {choiceCount} choices · search or pick</span></div>
            <div className="card-body"><div className="picker">
              <div className="scanbox plain"><Icon name="filter" /><input className="input" type="search" placeholder="Describe it: invoice, label, crossed, wrong order…" aria-label="Search what is wrong" value={search} onChange={(event) => setSearch(event.target.value)} /></div>
              {!!picker?.suggestions.length && <div className="sugg"><span className="lbl">Suggested for this unit</span>{picker.suggestions.map((s) => { const target = choices.find((c) => c.choice === s.choice); return <button key={s.choice} type="button" className={`chip${choiceId === s.choice ? " on" : ""}`} title={humanise(s.rule)} onClick={() => target && pick(target)}>{s.label}</button>; })}</div>}
              <div role="radiogroup" aria-labelledby="wb-what">
                {picker?.groups.map((group) => <details key={group.area} open={!!search || group.choices.some((c) => c.choice === choiceId) || group === picker.groups[0]}>
                  <summary>{group.area}<span className="n">{group.choices.length}</span></summary>
                  {group.choices.map((c) => <label key={c.choice} className={`opt${choiceId === c.choice ? " sel" : ""}`}>
                    <input type="radio" name="wb-choice" checked={choiceId === c.choice} onChange={() => pick(c)} />
                    <span>{choiceId === c.choice ? <strong>{c.label}</strong> : c.label}</span>
                    <span className="tag">{c.standard ? "" : "added"}</span>
                    {choiceId === c.choice && <span className="cmd">{c.commands.map(commandName).join(" · ")}</span>}
                  </label>)}
                </details>)}
                {picker && <label className={`opt none${choice?.fallback ? " sel" : ""}`}><input type="radio" name="wb-choice" checked={!!choice?.fallback} onChange={() => pick(picker.fallback)} /><span>{picker.fallback.label}</span><span className="tag later">files a request</span></label>}
              </div>
            </div></div>
          </section>

          {choice && !choice.fallback && <section className="card" aria-labelledby="wb-narrow">
            <div className="card-header"><h2 id="wb-narrow">{choice.questions[0]?.prompt ?? "The command"}</h2><span className="meta">Step 2 of 3 · narrows to one command</span></div>
            <div className="card-body">
              {choice.questions.length > 0 && <div className="segmented" role="group" aria-label="Which" style={{ marginBottom: 14, flexWrap: "wrap" }}>{options.map((o) => <button key={o.value} type="button" aria-pressed={option?.value === o.value} onClick={() => { setOption(o); setPreview(null); setArgs({}); setCommandId(newCommandId()); }}>{o.label}</button>)}</div>}
              {command && <CommandForm command={command} unitId={unitId} primary={primary?.rawValue ?? null} openTypes={openTypes} args={args} setArgs={setArgs} rawArgs={rawArgs} setRawArgs={setRawArgs} />}
            </div>
          </section>}

          {choice?.fallback && <section className="card" aria-labelledby="wb-fallback">
            <div className="card-header"><h2 id="wb-fallback">Describe the problem</h2><span className="meta">Step 2 of 2 · files a correction request</span></div>
            <div className="card-body"><div className="form-grid">
              <div className="field span"><label htmlFor="wb-description">What is wrong, in your words</label><textarea id="wb-description" className="textarea" value={description} onChange={(event) => setDescription(event.target.value)} /></div>
              <EvidenceField evidence={evidence} setEvidence={setEvidence} value={evidenceRef} setValue={setEvidenceRef} />
              <div className="span note info"><strong>Nothing changes yet.</strong>&nbsp;The serial administrator re-files the request under a listed choice, which then runs its command, or escalates it as "no command fits". It is counted once, under its real root cause.</div>
            </div></div>
          </section>}

          {choice && !choice.fallback && command && (INLINE.includes(command) || !DEDICATED_SCREENS[command]) && <section className="card" aria-labelledby="wb-preview">
            <div className="card-header"><h2 id="wb-preview">Preview <span className="meta">computed by the command, not the screen</span></h2><span className="meta">Step 3 of 3</span></div>
            {preview && <table className="table dense"><thead><tr><th>Will be recorded</th><th>Detail</th><th className="num">Count</th></tr></thead><tbody>
              {recordedRows(preview.preview).map((row) => <tr key={row.what}><td className="mono">{row.what}</td><td>{row.detail}</td><td className="num">{row.count}</td></tr>)}
              {preview.preview.units.filter((u) => u.outcome !== "change").map((u) => <tr key={u.unitId}><td className="serial">{u.serial ?? shortId(u.unitId)}</td><td><StatusBadge tone={outcomeTone(u.outcome)}>{humanise(u.outcome)}</StatusBadge> {u.reason ? humanise(u.reason) : ""}{u.unblockingStep ? ` · ${u.unblockingStep}` : ""}</td><td className="num">1</td></tr>)}
            </tbody></table>}
            <div className="card-body"><div className="form-grid">
              {!preview && !previewing && !previewError && <div className="span muted">Complete step 2 and choose a reason; the preview runs the real command and rolls it back.</div>}
              {previewing && <div className="span muted">Rehearsing the command…</div>}
              <ErrorNote error={previewError} />
              {preview?.preview.refusal && <div className="span note warn"><strong>{humanise(preview.preview.refusal.rule)}.</strong>&nbsp;{preview.preview.refusal.message}{preview.preview.refusal.unblockingStep ? ` · ${preview.preview.refusal.unblockingStep}` : ""}</div>}
              <div className="field"><label htmlFor="wb-reason">Reason code</label><select id="wb-reason" className="select" value={reason} onChange={(event) => setReason(event.target.value)}>{reasons.map((r) => <option key={r} value={r}>{r}</option>)}</select></div>
              <div className="field"><label htmlFor="wb-corrects">Corrects an earlier correction <span className="hint">· optional</span></label><select id="wb-corrects" className="select" value={corrects} onChange={(event) => setCorrects(event.target.value)}><option value="">none</option>{lineage?.corrections.map((c) => <option key={c.correctionId} value={c.correctionId}>{humanise(c.command)} · {fmtDate(c.eventTime)} · {c.reasonCode}</option>)}</select></div>
              <div className="field span"><label htmlFor="wb-note">Note</label><textarea id="wb-note" className="textarea" value={note} onChange={(event) => setNote(event.target.value)} /></div>
              <EvidenceField evidence={evidence} setEvidence={setEvidence} value={evidenceRef} setValue={setEvidenceRef} />
            </div></div>
          </section>}

          {request && <RequestOutcome request={request} />}
        </div>
      </div>
    </main>
    <Rail label="Routing">
      <Section title="This choice">{choice ? <Kv rows={[["Choice", choice.label], ["Command", command ? commandName(command) : "—"], ["May initiate", (choice.initiateRoles ?? []).map(humanise).join(", ") || "any member"], ["May commit", (choice.commitRoles ?? []).map(humanise).join(", ") || "—"], ["Root cause", String(choice.rootCause)]]} /> : <p className="muted">Pick what is wrong to see who may initiate and who must approve.</p>}</Section>
      <Section title="Correction screens"><div className="act" style={{ display: "grid", gap: 6 }}><Link className="btn block" to="/corrections/kpi">Correction volume</Link><Link className="btn block" to="/corrections/merge">Merge units</Link><Link className="btn block" to="/corrections/reassign-allocation">Reassign allocation</Link><Link className="btn block" to="/corrections/vendor-serials">Assign vendor serials</Link></div></Section>
      <Section title="Rules applied"><p className="muted">The preview is the command's own dry run. Submitting stores this exact preview; an approver sees the same one, and the command runs only if it is unchanged when they approve.</p></Section>
    </Rail>
  </>;
}

function CommandForm({ command, unitId, primary, openTypes, args, setArgs, rawArgs, setRawArgs }: {
  command: string; unitId: string | null; primary: string | null; openTypes: string[]; args: Record<string, string>; setArgs: (value: Record<string, string>) => void; rawArgs: string; setRawArgs: (value: string) => void;
}) {
  const set = (key: string, value: string) => setArgs({ ...args, [key]: value });
  const [other, setOther] = useState("");
  const [otherLabel, setOtherLabel] = useState<string | null>(null);
  const [error, setError] = useState<unknown>(null);
  const findOther = async () => {
    setError(null);
    try {
      const result = await workbenchApi.resolve({ kind: "identifier", value: other.trim() });
      const hit = result.candidates[0];
      if (!hit) throw new Error(`Nothing resolves from "${other.trim()}".`);
      setArgs({ ...args, other: hit.resolvedUnitId }); setOtherLabel(`${hit.displayIdentifier ?? shortId(hit.resolvedUnitId)} · ${statusLabel(hit.lifecycleStatus)}`);
    } catch (reason) { setError(reason); }
  };
  const typeSelect = <div className="field"><label htmlFor="wb-type">Identifier type</label><select id="wb-type" className="select" value={args.type ?? ""} onChange={(event) => set("type", event.target.value)}><option value="">Choose…</option>{openTypes.map((t) => <option key={t} value={t}>{t}</option>)}</select></div>;
  const dedicated = DEDICATED_SCREENS[command];
  if (dedicated) return <div className="note info"><strong>{commandName(command)}</strong>&nbsp;has its own screen with the command's full preview.&nbsp;<Link className="btn primary" to={`${dedicated.path}${dedicated.path.includes("?") ? "&" : "?"}unit=${unitId ?? ""}`}>Open {dedicated.label}</Link></div>;
  switch (command) {
    case "correct_identifier": return <div className="form-grid">{typeSelect}<div className="field"><label htmlFor="wb-value">Correct value</label><input id="wb-value" className="input serial" value={args.value ?? ""} onChange={(event) => set("value", event.target.value)} /></div><div className="span hint">CorrectIdentifier · the wrong row closes with superseded_by; the corrected row opens. The old value stays searchable.</div></div>;
    case "swap_identifiers": return <><div className="pair" style={{ marginBottom: 12 }}>
      <div className="side"><div className="k">This unit</div><div className="serial">{primary ?? shortId(unitId)}</div></div><div className="arrow">⇄</div>
      <div className="side"><div className="k">Other unit · scan</div><form onSubmit={(event) => { event.preventDefault(); void findOther(); }}><input className="input serial" aria-label="Other unit" value={other} onChange={(event) => setOther(event.target.value)} placeholder="Scan the other label" /></form>{otherLabel && <div className="hint">{otherLabel}</div>}</div>
    </div><div className="form-grid">{typeSelect}<div className="span hint">SwapIdentifiers · both identifiers move in one transaction; uniqueness is checked at the end; entitlements stay with each physical unit.</div><ErrorNote error={error} /></div></>;
    case "void_unit": return <div className="form-grid"><div className="field span"><label htmlFor="wb-order">Production order id <span className="hint">· optional</span></label><input id="wb-order" className="input serial" value={args.order ?? ""} onChange={(event) => set("order", event.target.value)} placeholder="UUID" /></div><div className="span hint">VoidUnit · lifecycle becomes void, never a delete; refused while components are still issued or once the unit has shipped.</div></div>;
    case "correct_item": return <div className="form-grid"><div className="field span"><label htmlFor="wb-item">Correct item id</label><input id="wb-item" className="input serial" value={args.item ?? ""} onChange={(event) => set("item", event.target.value)} placeholder="UUID" /></div><div className="span hint">CorrectItem · a new effective-dated item assignment; in stock it is paired with the inventory reclassification.</div></div>;
    default: return <div className="form-grid"><div className="field span"><label htmlFor="wb-raw">Command arguments <span className="hint">· {commandName(command)} · JSON with snake_case keys, as the command records them</span></label><textarea id="wb-raw" className="textarea serial" style={{ minHeight: 140 }} value={rawArgs} onChange={(event) => setRawArgs(event.target.value)} /></div><div className="span hint">This command has no dedicated form yet. The server still validates every argument and shows its own preview.</div></div>;
  }
}

function EvidenceField({ evidence, setEvidence, value, setValue }: { evidence: WorkbenchEvidenceRef[]; setEvidence: (value: WorkbenchEvidenceRef[]) => void; value: string; setValue: (value: string) => void }) {
  return <div className="field span"><label htmlFor="wb-evidence">Evidence <span className="hint">· vendor list, e-mail, photograph reference</span></label>
    {evidence.map((e, index) => <div key={`${e.ref}-${index}`} className="job"><div><div className="t">{e.ref}</div><div className="s">{e.kind}</div></div><button type="button" className="btn ghost" onClick={() => setEvidence(evidence.filter((_, i) => i !== index))}>Remove</button></div>)}
    <div style={{ display: "flex", gap: 8 }}><input id="wb-evidence" className="input" value={value} onChange={(event) => setValue(event.target.value)} placeholder="Paste a reference" /><button type="button" className="btn" disabled={!value.trim()} onClick={() => { setEvidence([...evidence, { kind: value.includes("@") ? "e-mail" : "reference", ref: value.trim() }]); setValue(""); }}>Add</button></div></div>;
}

function RequestOutcome({ request }: { request: WorkbenchRequestRow }) {
  const tone = request.status === "committed" || request.status === "approved" ? "ok" : request.status === "draft" || request.status === "rejected" ? "warn" : "info";
  const text: Record<string, string> = {
    awaiting_approval: `Waiting for ${request.requiredApproverRoles.map(humanise).join(" or ")}. The approver sees this exact preview.`,
    approved: "Approved. You may commit it now; the command re-runs its checks first.",
    committed: "Committed. The command recorded its events and the correction counts once in the KPI.",
    draft: "Saved as a draft: the command refuses it as it stands. Fix the refusal and submit again.",
    unclassified: "Filed. The serial administrator re-files it under a listed choice or escalates it.",
  };
  return <div className={`note ${tone}`}><strong>Request {shortId(request.requestId)} · {humanise(request.status)}.</strong>&nbsp;{text[request.status] ?? ""}&nbsp;<Link to={`/corrections/requests/${request.requestId}`}>Open request</Link></div>;
}

function aborted(reason: unknown): boolean { return reason instanceof DOMException && reason.name === "AbortError"; }
