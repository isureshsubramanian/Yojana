import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { Icon } from "../shell/Icons";
import { Link } from "react-router";
import { ApiError } from "../api/client";
import { DataGrid, FormActions, StatusBadge, useGridQuery, type GridColumn, sortCaption } from "../components";
import { useSession } from "../session/session";
import { ErrorNote } from "../units/common";
import { fmtDateTime } from "../units/format";
import { MigrationAccess, MigrationPage } from "./MigrationCommon";
import { migrationApi } from "./migrationApi";
import { batchLabel } from "./migrationModel";
import type {
  ConditionMappingRule, CurrencyInfo, LinkOverride, LinkOverrideUploadResponse,
  MappingPolicyChoice, MappingSettings as MappingSettingsView, ReplacementLinkValues,
} from "./migrationTypes";
import { linkOverridePage } from "./migrationTypes";

const directions = [["customer_facing", "Customer-facing"], ["vendor_facing", "Supplier-facing"]] as const;
const reasons = [["warranty_defect", "Warranty defect"], ["dead_on_arrival", "Dead on arrival"], ["seller_error", "Seller error"], ["goodwill", "Goodwill"], ["recall", "Recall"]] as const;
const states = [["closed_returned", "Closed, returned"], ["closed_billed", "Closed, billed"]] as const;
const conditions = [["any", "Any"], ["new", "New"], ["new_or_refurbished", "New or refurbished"]] as const;
const prices = [["zero", "No charge"], ["difference", "Difference charged"], ["full", "Full price charged"]] as const;
const grades = [["new", "New"], ["used", "Used"], ["defective", "Defective"], ["scrap", "Scrap"]] as const;
const cardStatuses = [["", "Any"], ["defective", "Defective"], ["scrap", "Scrap"]] as const;
const movements = [["", "Any"], ["customer_returned", "Returned by customer"], ["issued_out", "Issued out"], ["shipped", "Shipped"]] as const;

export function Component() { return <MigrationAccess><MigrationSettings /></MigrationAccess>; }

function MigrationSettings() {
  const session = useSession();
  const canEdit = session.roles.some(role => role === "tenant_administrator" || role === "migration_lead");
  const [saved, setSaved] = useState<MappingSettingsView | null>(null);
  const [draft, setDraft] = useState<MappingSettingsView | null>(null);
  const [currencies, setCurrencies] = useState<CurrencyInfo[]>([]);
  const [batches, setBatches] = useState<Awaited<ReturnType<typeof migrationApi.batches>>["items"]>([]);
  const [batchId, setBatchId] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [serverRule, setServerRule] = useState<string | null>(null);
  const [notice, setNotice] = useState("");
  const [unusedOnly, setUnusedOnly] = useState(false);
  const [upload, setUpload] = useState<LinkOverrideUploadResponse | null>(null);
  const [uploadName, setUploadName] = useState("");
  const [confirmClear, setConfirmClear] = useState(false);
  const fileInput = useRef<HTMLInputElement>(null);

  const load = useCallback(async (signal?: AbortSignal) => {
    setError(null);
    try {
      const [next, currencyRows, batchPage] = await Promise.all([
        migrationApi.mappingSettings(signal), migrationApi.currencyCatalogue(signal), migrationApi.batches(undefined, signal),
      ]);
      setSaved(next); setDraft(copy(next)); setCurrencies(currencyRows); setBatches(batchPage.items);
      setBatchId(current => batchPage.items.some(row => row.batchId === current) ? current : batchPage.items[0]?.batchId ?? "");
    } catch (reason) { if (!(reason instanceof DOMException && reason.name === "AbortError")) setError(reason); }
  }, []);

  useEffect(() => { const controller = new AbortController(); void load(controller.signal); return () => controller.abort(); }, [load]);

  const overrideParams = useMemo(() => { const value = new URLSearchParams(); if (batchId) value.set("batchId", batchId); if (unusedOnly) value.set("unused", "true"); return value; }, [batchId, unusedOnly]);
  const overrides = useGridQuery("/api/migration/link-overrides", linkOverridePage, {
    gridId: "migration-link-overrides", userId: session.user.id,
    defaultSort: { key: "document", dir: "asc" }, defaultSize: 12, params: overrideParams,
    paging: "cursor", sortKeys: ["document", "uploaded"], pageFacts: value => ({ continuation: value.nextCursor }),
  });
  const overrideGrid = { rows: overrides.data?.items ?? [], total: unusedOnly ? overrides.data?.unusedCount ?? 0 : overrides.data?.totalCount ?? 0,
    page: overrides.page, pageSize: overrides.pageSize, continuation: overrides.data?.nextCursor ?? null, totalIsExact: true, hasMore: !!overrides.data?.nextCursor };
  const linkErrors = draft ? validateLinks(draft.linkDefaults) : {};
  const dirty = !!saved && !!draft && JSON.stringify(editable(saved)) !== JSON.stringify(editable(draft));

  const save = async () => {
    if (!draft || Object.keys(linkErrors).length) return;
    setBusy(true); setError(null); setServerRule(null); setNotice("");
    try {
      const next = await migrationApi.saveMappingSettings(draft);
      setSaved(next); setDraft(copy(next)); setNotice("Migration settings saved. Map again to apply them.");
    } catch (reason) {
      setError(reason); setServerRule(reason instanceof ApiError ? reason.rule : null);
    } finally { setBusy(false); }
  };

  const uploadCsv = async (file: File) => {
    setBusy(true); setError(null); setUpload(null); setUploadName(file.name); setConfirmClear(false);
    try { const result = await migrationApi.uploadLinkOverrides(file); setUpload(result); if (!result.errorTotal) overrides.reload(); }
    catch (reason) { setError(reason); }
    finally { setBusy(false); if (fileInput.current) fileInput.current.value = ""; }
  };

  const clear = async () => {
    setBusy(true); setError(null);
    try {
      const result = await migrationApi.removeLinkOverrides();
      setUpload(null); setConfirmClear(false); setNotice(`${result.removed.toLocaleString()} overrides removed.`); overrides.reload();
    } catch (reason) { setError(reason); }
    finally { setBusy(false); }
  };

  const commit = canEdit && draft ? <FormActions status={notice}>
    <button type="button" className="btn primary" disabled={busy || !dirty || Object.keys(linkErrors).length > 0} onClick={() => void save()}>{busy ? "Saving…" : "Save changes"}</button>
    <button type="button" className="btn" disabled={busy || !dirty} onClick={() => { setDraft(copy(saved!)); setServerRule(null); }}>Discard</button>
  </FormActions> : null;
  const access = <p className="page-header-note"><strong>Who can change these:</strong> tenant administrators and the Migration lead. Every saved setting and upload records who made it and when.</p>;

  const columns = useMemo<GridColumn<LinkOverride>[]>(() => [
    { key: "document", label: "Document number", width: 23, sort: { key: "document", defaultDir: "asc" }, render: row => <span className="primary">{row.sourceKey}</span> },
    { key: "values", label: "Overridden values", width: 38, render: row => overrideSummary(row) },
    { key: "upload", label: "Last upload", width: 25, sort: { key: "uploaded", defaultDir: "desc" }, render: row => <span>{row.uploadFileName}<span className="sub">{fmtDateTime(row.uploadedAt)} · {row.uploadedByName}</span></span> },
    { key: "use", label: "Use", width: 14, render: row => row.unused ? <StatusBadge tone="warn">Unused</StatusBadge> : <StatusBadge tone="ok">In plan</StatusBadge> },
  ], []);

  return <MigrationPage title="Migration settings" eyebrow="Changes apply from the next mapping run" sub="How the load fills what the source application cannot tell. Nothing here changes a unit that is already loaded: map again and run a new rehearsal to see the effect." actions={access}>
    <ErrorNote error={error} />
    {!canEdit && <div className="note">You have read-only access. Tenant administrators and the Migration lead can change these settings.</div>}
    {!draft && !error && <p role="status">Loading migration settings…</p>}
    {draft && <div className="migration-settings-layout">
      <div className="stack">
        <CoverCard value={draft} setValue={setDraft} readOnly={!canEdit || busy} serverRule={serverRule} />
        <LinkDefaultsCard value={draft.linkDefaults} currencies={currencies} readOnly={!canEdit || busy} errors={linkErrors} serverRule={serverRule} onChange={linkDefaults => setDraft({ ...draft, linkDefaults })} />
      </div>
      <div className="stack">
        <MappingCard value={draft} setValue={setDraft} readOnly={!canEdit || busy} serverRule={serverRule} />
        <section className="card migration-overrides" aria-labelledby="migration-overrides-title">
          <div className="card-header"><h2 id="migration-overrides-title">Per-link overrides</h2><div className="right"><StatusBadge tone="neutral">{(overrides.data?.totalCount ?? 0).toLocaleString()} links</StatusBadge></div></div>
          <div className="card-body stack">
            <p className="muted">Download the links of the selected mapping run with the values each one will get, change the rows that differ, and upload the file. Rows are matched by the incumbent document number, so overrides survive a new extraction. Leave a row’s value columns empty to remove its override.</p>
            <label className="field"><span className="lbl">Extraction</span><select className="select" value={batchId} onChange={event => setBatchId(event.target.value)}>{batches.map((batch, index) => <option key={batch.batchId} value={batch.batchId}>{batchLabel(batch, index === 0)}</option>)}</select></label>
            <div className="migration-settings-actions">
              <a className={`btn${batchId ? "" : " disabled"}`} aria-disabled={!batchId} href={batchId ? migrationApi.linkExportUrl(batchId) : undefined}>Download links (CSV)</a>
              {canEdit && <><input ref={fileInput} className="visually-hidden" type="file" accept=".csv,text/csv" aria-label="Upload overrides CSV" onChange={event => { const file = event.target.files?.[0]; if (file) void uploadCsv(file); }} /><button type="button" className="btn primary" disabled={busy} onClick={() => fileInput.current?.click()}>Upload overrides</button><button type="button" className="btn" disabled={busy || (overrides.data?.totalCount ?? 0) === 0} onClick={() => setConfirmClear(true)}>Remove all overrides</button></>}
            </div>
            <div className="migration-override-summary"><span className="k">Unused</span><span className="v">{(overrides.data?.unusedCount ?? 0).toLocaleString()} overrides whose link is not in the selected mapping run</span>{upload && !upload.errorTotal && <><span className="k">Result</span><span className="v">{upload.added.toLocaleString()} added · {upload.changed.toLocaleString()} changed · {upload.removed.toLocaleString()} removed · {upload.unchanged.toLocaleString()} unchanged</span></>}</div>
            {confirmClear && <div className="migration-settings-confirm note warn"><span><strong>Remove every override?</strong> The tenant defaults will apply to every mapped link.</span><span><button type="button" className="btn danger sm" disabled={busy} onClick={() => void clear()}>Confirm removal</button><button type="button" className="btn sm" onClick={() => setConfirmClear(false)}>Keep overrides</button></span></div>}
          </div>
        </section>
        {upload && upload.errorTotal > 0 && <UploadErrors result={upload} fileName={uploadName} />}
      </div>
      <section className="card migration-overrides-table" aria-labelledby="migration-overrides-table-title">
        <div className="card-header"><h2 id="migration-overrides-table-title">Overrides</h2><label className="checkrow"><input type="checkbox" checked={unusedOnly} onChange={event => setUnusedOnly(event.target.checked)} /><span>Unused only</span></label></div>
        <ErrorNote error={overrides.error} /><DataGrid columns={columns} page={overrideGrid} rowId={row => row.sourceKey} onPage={overrides.onPage} loading={overrides.loading} emptyText={unusedOnly ? "No unused overrides." : "No per-link overrides."} viewportHeight={430}
          gridId="migration-link-overrides" userId={session.user.id} sort={overrides.sort} defaultSort={{ key: "document", dir: "asc" }} onSort={overrides.onSort}
          pageSize={overrides.pageSize} defaultPageSize={12} pageSizeOptions={[12, 25, 50]} onPageSize={overrides.onPageSize} footerNote={sortCaption(columns, overrides, "Sorted by document number")} />
        <div className="card-footer"><span className="muted">Server-paged in document-number order · an unused override is kept for a later mapping run</span></div>
      </section>
    </div>}
    {commit}
  </MigrationPage>;
}

function CoverCard({ value, setValue, readOnly, serverRule }: { value: MappingSettingsView; setValue: (value: MappingSettingsView) => void; readOnly: boolean; serverRule: string | null }) {
  const base = (value.approvedPublishedPolicies ?? []).filter(policy => policy.familyCode === "base_warranty");
  const supplier = (value.approvedPublishedPolicies ?? []).filter(policy => policy.familyCode === "supplier_warranty");
  return <section className="card" aria-labelledby="migration-cover-title"><div className="card-header"><h2 id="migration-cover-title">Cover with no contract</h2><span className="meta">Only approved and published policies are offered</span></div><div className="card-body migration-settings-form">
    <p className="muted">Some serials have warranty dates on the equipment card or serial master but no warranty contract. The dates stay exactly as the source application holds them; the policy supplies the terms.</p>
    <PolicyField label="Dates from the equipment card only · our warranty to the customer" value={value.cardCoverPolicyId} policies={base} readOnly={readOnly} unset="Not set: Equipment-card cover policy not set" help="Base warranty family" onChange={cardCoverPolicyId => setValue({ ...value, cardCoverPolicyId })} />
    <PolicyField label="Dates from the serial master only · the manufacturer’s warranty to us" value={value.serialMasterCoverPolicyId} policies={supplier} readOnly={readOnly} unset="Not set: Serial-master cover policy not set" help="Supplier cover family, recorded as inbound" onChange={serialMasterCoverPolicyId => setValue({ ...value, serialMasterCoverPolicyId })} />
    {serverRule?.includes("cover_policy") && <span className="form-error" role="alert">The selected policy is not an approved published policy in the required coverage family.</span>}
  </div><div className="card-footer"><span className="muted">Unset choices remain visible as cover gaps.</span><span className="grow" /><Link to="/integration/migration/validation">See the gaps</Link></div></section>;
}

function PolicyField({ label, value, policies, readOnly, unset, help, onChange }: { label: string; value: string | null; policies: MappingPolicyChoice[]; readOnly: boolean; unset: string; help: string; onChange: (value: string | null) => void }) {
  return <label className="field"><span className="lbl">{label}</span><select className="select" disabled={readOnly} value={value ?? ""} onChange={event => onChange(event.target.value || null)}><option value="">{unset}</option>{value && !policies.some(policy => policy.policyId === value) && <option value={value}>Previously selected policy is no longer published</option>}{policies.map(policy => <option value={policy.policyId} key={policy.policyId}>{policy.policyName} · version {policy.version} ({policy.policyCode})</option>)}</select><span className="help">{help}</span></label>;
}

function LinkDefaultsCard({ value, currencies, readOnly, errors, serverRule, onChange }: { value: ReplacementLinkValues; currencies: CurrencyInfo[]; readOnly: boolean; errors: Record<string, string>; serverRule: string | null; onChange: (value: ReplacementLinkValues) => void }) {
  const field = <K extends keyof ReplacementLinkValues>(key: K, next: ReplacementLinkValues[K]) => onChange({ ...value, [key]: next });
  const charged = value.pricePolicy !== "zero";
  const serverField = linkRuleField(serverRule);
  return <section className="card" aria-labelledby="migration-link-defaults-title"><div className="card-header"><h2 id="migration-link-defaults-title">Replacement links: values the source application does not hold</h2></div><div className="card-body"><div className="form-grid">
    <Choice label="Direction" value={value.direction} options={directions} readOnly={readOnly} onChange={next => field("direction", next)} />
    <Choice label="Reason" value={value.replacementReason} options={reasons} readOnly={readOnly} onChange={next => field("replacementReason", next)} />
    <Choice label="State" value={value.state} options={states} readOnly={readOnly} help="A migrated link is history, so only closed states" onChange={next => field("state", next)} />
    <Choice label="Replacement condition allowed" value={value.replacementConditionAllowed} options={conditions} readOnly={readOnly} onChange={next => field("replacementConditionAllowed", next)} />
    <Choice label="Price" value={value.pricePolicy} options={prices} readOnly={readOnly} onChange={next => onChange({ ...value, pricePolicy: next, ...(next === "zero" ? { differenceAmount: 0, currency: "XXX" } : {}) })} />
    <label className="field"><span className="lbl">Amount</span><input className="input num" type="number" min="0" step="0.01" disabled={readOnly || !charged} value={value.differenceAmount} onChange={event => field("differenceAmount", Number(event.target.value))} />{(errors.differenceAmount || serverField === "differenceAmount") && <span className="form-error" role="alert">{errors.differenceAmount ?? "The amount is not valid for the selected price."}</span>}</label>
    <label className="field"><span className="lbl">Currency</span><select className="select" disabled={readOnly || !charged} value={value.currency} onChange={event => field("currency", event.target.value)}><option value="XXX">No currency</option>{currencies.map(currency => <option key={currency.code} value={currency.code}>{currency.code} · {currency.name}</option>)}</select>{(errors.currency || serverField === "currency") && <span className="form-error" role="alert">{errors.currency ?? "The currency is not valid for this amount."}</span>}</label>
  </div>{serverField && !["differenceAmount", "currency"].includes(serverField) && <span className="form-error" role="alert">A link default is not valid. Review {label(serverField).toLowerCase()}.</span>}</div><div className="card-footer"><span className="muted">Defaults apply to closed–returned and closed–billed links only, when the source link has no override.</span></div></section>;
}

function Choice({ label: text, value, options, readOnly, help, onChange }: { label: string; value: string; options: readonly (readonly [string, string])[]; readOnly: boolean; help?: string; onChange: (value: string) => void }) {
  return <label className="field"><span className="lbl">{text}</span><select className="select" value={value} disabled={readOnly} onChange={event => onChange(event.target.value)}>{options.map(([key, name]) => <option value={key} key={key}>{name}</option>)}</select>{help && <span className="help">{help}</span>}</label>;
}

function MappingCard({ value, setValue, readOnly, serverRule }: { value: MappingSettingsView; setValue: (value: MappingSettingsView) => void; readOnly: boolean; serverRule: string | null }) {
  const update = (index: number, rule: ConditionMappingRule) => setValue({ ...value, conditionMap: value.conditionMap.map((row, rowIndex) => rowIndex === index ? rule : row) });
  return <section className="card" aria-labelledby="migration-mapping-title"><div className="card-header"><h2 id="migration-mapping-title">Mapping</h2></div><div className="card-body migration-settings-form">
    <label className="field"><span className="lbl">Cutover date</span><input className="input" type="date" disabled={readOnly} value={value.cutoverDate ?? ""} onChange={event => setValue({ ...value, cutoverDate: event.target.value || null })} /><span className="help">Cover ending before this date loads as expired, with its real dates</span></label>
    <label className="checkrow"><input type="checkbox" disabled={readOnly} checked={value.allowWaitingReviews} onChange={event => setValue({ ...value, allowWaitingReviews: event.target.checked })} /><span>Map while review-queue items are still waiting<span className="hint">Off: mapping stops until every item is decided. On: waiting items become gaps.</span></span></label>
    <div className="field"><span className="lbl">Condition of loaded serials · first matching rule wins</span><div className="grid-viewport migration-condition-rules"><table className="table dense"><colgroup><col /><col className="movement-col" /><col /><col className="actions-col" /></colgroup><thead><tr><th>Card status</th><th>Last movement</th><th>Condition</th><th><span className="visually-hidden">Actions</span></th></tr></thead><tbody>{value.conditionMap.map((rule, index) => <tr key={index}><td><RuleChoice value={rule.cardStatus ?? ""} options={cardStatuses} readOnly={readOnly} onChange={cardStatus => update(index, { ...rule, cardStatus: cardStatus || null })} /></td><td><RuleChoice value={rule.lastMovementKind ?? ""} options={movements} readOnly={readOnly} onChange={lastMovementKind => update(index, { ...rule, lastMovementKind: lastMovementKind || null })} /></td><td><RuleChoice value={rule.grade} options={grades} readOnly={readOnly} onChange={grade => update(index, { ...rule, grade })} /></td><td className="actions">{!readOnly && index < value.conditionMap.length - 1 && <button type="button" className="icon-btn" title="Remove rule" aria-label={`Remove rule ${index + 1}`} onClick={() => setValue({ ...value, conditionMap: value.conditionMap.filter((_, rowIndex) => rowIndex !== index) })}><Icon name="x" /></button>}</td></tr>)}</tbody></table></div>{!readOnly && <button type="button" className="btn sm" onClick={() => setValue({ ...value, conditionMap: [...value.conditionMap.slice(0, -1), { cardStatus: null, lastMovementKind: "shipped", grade: "new" }, value.conditionMap.at(-1)!] })}>Add a rule</button>}<span className="help">The last rule always matches.</span></div>
    {serverRule?.includes("condition_") && <span className="form-error" role="alert">Provide supported conditions and keep an “Any / Any” rule last.</span>}
  </div></section>;
}

function RuleChoice({ value, options, readOnly, onChange }: { value: string; options: readonly (readonly [string, string])[]; readOnly: boolean; onChange: (value: string) => void }) {
  return <select className="select" value={value} disabled={readOnly} onChange={event => onChange(event.target.value)}>{!options.some(([key]) => key === value) && <option value={value}>{label(value)}</option>}{options.map(([key, name]) => <option value={key} key={key}>{name}</option>)}</select>;
}

function UploadErrors({ result, fileName }: { result: LinkOverrideUploadResponse; fileName: string }) {
  return <section className="card" aria-labelledby="migration-upload-errors"><div className="card-header"><h2 id="migration-upload-errors">Upload refused <span className="meta">nothing was stored</span></h2></div><table className="table"><thead><tr><th className="num">Row</th><th>Column</th><th>Problem</th></tr></thead><tbody>{result.errors.map((error, index) => <tr key={`${error.row}-${error.column}-${index}`}><td className="num">{error.row.toLocaleString()}</td><td>{overrideColumnLabel(error.column)}</td><td className="wrap">{overrideProblemLabel(error.reason)}</td></tr>)}</tbody></table><div className="card-footer"><span className="muted">{fileName} · {result.errors.length.toLocaleString()} of {result.errorTotal.toLocaleString()} problems shown · fix the file and upload again</span></div></section>;
}

function copy(value: MappingSettingsView): MappingSettingsView { return { ...value, conditionMap: value.conditionMap.map(rule => ({ ...rule })), linkDefaults: { ...value.linkDefaults }, approvedPublishedPolicies: [...(value.approvedPublishedPolicies ?? [])] }; }
function editable(value: MappingSettingsView) { return { allowWaitingReviews: value.allowWaitingReviews, cutoverDate: value.cutoverDate, conditionMap: value.conditionMap, linkDefaults: value.linkDefaults, cardCoverPolicyId: value.cardCoverPolicyId, serialMasterCoverPolicyId: value.serialMasterCoverPolicyId }; }
function validateLinks(value: ReplacementLinkValues): Record<string, string> {
  const errors: Record<string, string> = {};
  if (!Number.isFinite(value.differenceAmount) || value.differenceAmount < 0) errors.differenceAmount = "Amount cannot be negative.";
  else if (value.pricePolicy === "zero" && value.differenceAmount !== 0) errors.differenceAmount = "No-charge links must have a zero amount.";
  if (value.differenceAmount > 0 && value.currency === "XXX") errors.currency = "A charged amount needs a currency.";
  return errors;
}
function linkRuleField(rule: string | null): string | null {
  if (!rule) return null;
  if (rule.includes("direction")) return "direction";
  if (rule.includes("reason")) return "replacementReason";
  if (rule.includes("state")) return "state";
  if (rule.includes("condition")) return "replacementConditionAllowed";
  if (rule.includes("price_policy")) return "pricePolicy";
  if (rule.includes("difference_amount") || rule.includes("zero_price")) return "differenceAmount";
  if (rule.includes("currency")) return "currency";
  return null;
}
function label(value: string): string { return value.replace(/_/g, " ").replace(/^./, character => character.toUpperCase()); }
function optionLabel(value: string | null, options: readonly (readonly [string, string])[]): string { return value ? options.find(([key]) => key === value)?.[1] ?? label(value) : "Default"; }
function overrideSummary(row: LinkOverride): string {
  return [row.direction && optionLabel(row.direction, directions), row.replacementReason && optionLabel(row.replacementReason, reasons), row.state && optionLabel(row.state, states), row.replacementConditionAllowed && optionLabel(row.replacementConditionAllowed, conditions), row.pricePolicy && optionLabel(row.pricePolicy, prices), row.differenceAmount !== null && `${row.differenceAmount.toLocaleString()} ${row.currency ?? ""}`.trim(), row.currency && row.differenceAmount === null && row.currency].filter(Boolean).join(" · ") || "Uses every tenant default";
}
export function overrideColumnLabel(value: string): string { return ({ source_key: "Document number", replacement_reason: "Reason", replacement_condition_allowed: "Replacement condition", price_policy: "Price", difference_amount: "Amount", file: "File" } as Record<string, string>)[value] ?? label(value); }
export function overrideProblemLabel(value: string): string { return ({ invalid_utf8: "The file is not valid UTF-8 text.", invalid_csv: "The CSV quoting or row structure is invalid.", header_required: "The file needs a header row.", column_required: "This required column is missing.", source_key_invalid: "The document number is blank or too long.", source_key_repeated: "The document number appears more than once.", source_key_unknown: "No replacement link with this document number has been mapped.", difference_amount_invalid: "Enter the amount as a number.", replacement_direction_unknown: "Choose Customer-facing or Supplier-facing.", replacement_reason_unknown: "Choose one of the listed reasons.", replacement_state_not_terminal: "A migrated link must be Closed, returned or Closed, billed.", replacement_condition_allowed_unknown: "Choose one of the listed replacement conditions.", replacement_price_policy_unknown: "Choose one of the listed price choices.", replacement_difference_amount_negative: "Amount cannot be negative.", replacement_zero_price_difference: "A no-charge link must have a zero amount.", replacement_currency_invalid: "Use a valid three-letter currency code.", replacement_currency_required: "A charged amount needs a currency." } as Record<string, string>)[value] ?? label(value); }
