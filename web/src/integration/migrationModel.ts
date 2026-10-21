import type { Tone } from "../components/types";
import type {
  MigrationBatch, MigrationBatchTable, MigrationOutcome, MigrationProfile, MigrationProfileSample,
  MigrationReviewDecision, MigrationReviewDetail, MigrationReviewKind, MigrationReviewStatus,
} from "./migrationTypes";

export const OUTCOMES: readonly MigrationOutcome[] = ["stop_load", "needs_person", "fixed_by_rule", "loaded_with_note"];

const outcomeLabels: Record<MigrationOutcome, string> = {
  stop_load: "Stop the load", needs_person: "Go to a person", fixed_by_rule: "Fixed by a rule", loaded_with_note: "Loaded with a note",
};
const outcomeTones: Record<MigrationOutcome, Tone> = { stop_load: "bad", needs_person: "warn", fixed_by_rule: "ok", loaded_with_note: "neutral" };

export const profileClasses: Record<string, { label: string; explanation: string; action: string; hardStop?: boolean }> = {
  serial_in_multiple_warehouses: { label: "Serial in two warehouses at once", explanation: "Per-warehouse quantity is above one in total.", action: "Fix the stock count or transfer in the source application, then extract again.", hardStop: true },
  negative_quantity: { label: "Negative quantity", explanation: "Per-warehouse quantity is below zero.", action: "Correct the source stock quantity, then extract again.", hardStop: true },
  duplicate_serial_same_item: { label: "Same serial on one item, twice", explanation: "Duplicate after trimming and case normalisation.", action: "Choose which source row represents the unit." },
  duplicate_serial_different_items: { label: "Same serial on different items", explanation: "A serial is never merged across items.", action: "Correct the item or serial in the source application." },
  serial_whitespace_or_case: { label: "Whitespace or mixed case", explanation: "Leading, trailing or inner spaces, or lower-case text.", action: "The load trims spaces and uses upper case." },
  serial_format_variants: { label: "Hyphen or leading-zero variants", explanation: "Variants are compared within the item family's serial pattern.", action: "Check the proposed normalisation rule before loading." },
  placeholder_serial: { label: "Placeholder serials", explanation: "Blank rows on items with system-assigned serials.", action: "The row is loaded and marked as unverified." },
  equipment_card_without_serial: { label: "Equipment card with no serial", explanation: "The equipment card has no matching serial-master row.", action: "Link or correct the card in the source application." },
  shipped_serial_without_card: { label: "Shipped serial with no card", explanation: "The unit was delivered but never received an equipment card.", action: "The delivery supplies ownership; review cover separately." },
  card_customer_delivery_mismatch: { label: "Card customer differs from last delivery", explanation: "This can indicate resale, a dealer, or a stale card.", action: "The load keeps both facts and records the mismatch." },
  active_contract_past_end: { label: "Contract active past its end date", explanation: "No termination date was recorded.", action: "The load preserves the contract and records the inconsistency." },
  ambiguous_dates: { label: "Ambiguous dates", explanation: "Day and month are both 12 or less in imported text.", action: "Confirm the intended date before relying on it." },
  serials_in_user_fields: { label: "Serials hiding in user fields", explanation: "Items are not managed by serial in the source configuration.", action: "Approve a discovery rule before this class can be counted." },
  empty_status: { label: "Status column empty", explanation: "Availability comes from quantity, never from this status.", action: "The load ignores the empty status and uses per-warehouse quantity." },
};


const factLabels: Record<string, string> = {
  system_number: "System number", total_quantity: "Total quantity", warehouse_count: "Warehouses", warehouse: "Warehouse",
  quantity: "Quantity", normalised: "Normalised serial", matching_rows: "Matching rows", match_rule: "Match rule",
  item_count: "Items", item_family: "Item family", variant_count: "Variants", normalisation: "Normalisation",
  management_method: "Management method", card_id: "Equipment card", customer: "Customer", reason: "Reason",
  document_type: "Document type", document_number: "Document number", party: "Customer", card_customer: "Card customer",
  last_delivery_customer: "Last delivery customer", last_document_number: "Last document", contract_id: "Contract",
  end_date: "End date", status: "Status", as_of: "Checked at", availability_basis: "Availability basis",
};

const valueLabels: Record<string, string> = {
  trimmed_upper_case: "Trimmed and upper-cased", remove_hyphens_spaces_and_leading_zeroes: "Remove hyphens, spaces and leading zeroes",
  system_assigned: "System assigned", no_serial_master_match: "No serial-master match", per_warehouse_quantity: "Per-warehouse quantity",
};

const failureLabels: Record<string, string> = {
  migration_extraction_licence_unconfirmed: "Licence position is not confirmed for this source.",
  migration_source_runtime_unsupported: "This source type cannot be extracted in this build.",
  migration_extraction_failed: "The extraction failed. Check the source connection and try again.",
  migration_extraction_cancelled: "The extraction was cancelled.",
};

export const REVIEW_KINDS: readonly MigrationReviewKind[] = ["same_serial_one_item", "same_serial_different_items", "orphan_equipment_card", "serial_in_user_field"];
export const REVIEW_STATUSES: readonly MigrationReviewStatus[] = ["waiting", "decided", "carried_over"];
export function mayReadMigration(roles: readonly string[]): boolean { return roles.some(role => role === "tenant_administrator" || role === "migration_lead"); }
export function mayAdministerMigration(roles: readonly string[]): boolean { return roles.includes("tenant_administrator"); }

const reviewKindLabels: Record<MigrationReviewKind, string> = {
  same_serial_one_item: "Same serial on one item",
  same_serial_different_items: "Same serial on different items",
  orphan_equipment_card: "Orphan equipment card",
  serial_in_user_field: "Serial in a user field",
};
const reviewStatusLabels: Record<MigrationReviewStatus, string> = { waiting: "Waiting", decided: "Decided", carried_over: "Carried over" };
const reviewDecisionLabels: Record<MigrationReviewDecision, string> = { merge_into: "Merge into", keep_apart: "Keep apart", fix_in_incumbent: "Fix in the incumbent" };
const matchRuleLabels: Record<string, string> = {
  exact: "Exact",
  trimmed_upper: "Trimmed and upper-cased",
  family_normalised: "Normalised for the item family",
  reviewed: "Reviewed by a person",
  no_serial_match: "No serial match",
};
const canonicalisationStatusLabels: Record<string, string> = { queued: "Queued", running: "Running", completed: "Completed", failed: "Failed", not_run: "Not run" };
const refusalRuleLabels: Record<string, string> = {
  migration_canonicalisation_already_running: "Canonicalisation is already running",
  migration_batch_not_found: "Extraction not found",
  migration_canonicalisation_batch_not_succeeded: "Extraction has not succeeded",
  migration_canonicalisation_profile_blocked: "Profiling blocks canonicalisation",
  migration_canonicalisation_profile_missing: "Profiling has not run",
  migration_canonicalisation_profile_running: "Profiling is still running",
  migration_canonicalisation_profile_not_completed: "Profiling did not complete",
  migration_review_reason_required: "Decision reason required",
  migration_review_note_too_long: "Decision note is too long",
  migration_review_item_not_found: "Review item not found",
  migration_review_note_required: "Correction note required",
  migration_review_keep_candidate_invalid: "Candidate is not available",
  migration_cross_item_merge_forbidden: "Merge across items is not allowed",
  migration_review_decision_invalid: "Decision is not valid",
  migration_cursor_invalid: "Review page is no longer available",
  audit_actor_required: "Signed-in person is required",
};

export function outcomeLabel(value: MigrationOutcome): string { return outcomeLabels[value]; }
export function outcomeTone(value: MigrationOutcome): Tone { return outcomeTones[value]; }
export function profileClass(code: string) { return profileClasses[code] ?? { label: readable(code), explanation: "Profiling query result.", action: "Review the sample rows before loading." }; }
export function factLabel(code: string): string { return factLabels[code] ?? readable(code); }
export function factValue(value: string | null): string { return value === null || value === "" ? "—" : valueLabels[value] ?? readableValue(value); }
export function failureLabel(code: string | null): string | null { return code ? failureLabels[code] ?? readable(code) : null; }
export function runtimeLabel(value: string): string { return value === "sql_server" ? "SQL Server" : readable(value); }
export function routeLabel(value: string): string { return ({ separate: "Separate connection", cross_database: "Cross-database connection", linked_server: "Linked-server connection" })[value] ?? readable(value); }
export function batchStatusLabel(value: string): string { return ({ queued: "Queued", running: "Running", succeeded: "Succeeded", failed: "Failed", refused: "Refused" })[value] ?? readable(value); }
export function profileStatusLabel(value: string): string { return ({ queued: "Queued", running: "Running", completed: "Completed", blocked: "Blocked", failed: "Failed" })[value] ?? readable(value); }
export function reviewKindLabel(value: MigrationReviewKind): string { return reviewKindLabels[value] ?? readable(value); }
export function reviewStatusLabel(value: MigrationReviewStatus): string { return reviewStatusLabels[value] ?? readable(value); }
export function reviewDecisionLabel(value: MigrationReviewDecision): string { return reviewDecisionLabels[value] ?? readable(value); }
export function matchRuleLabel(value: string | null | undefined): string { return value ? matchRuleLabels[value] ?? readable(value) : "—"; }
export function canonicalisationStatusLabel(value: string): string { return canonicalisationStatusLabels[value] ?? readable(value); }
export function refusalRuleLabel(value: string | null | undefined): string { return value ? refusalRuleLabels[value] ?? readable(value) : "Request refused"; }
export function statusTone(value: string): Tone { return /^(succeeded|completed)$/.test(value) ? "ok" : /^(failed|refused|blocked)$/.test(value) ? "bad" : /^(queued|running)$/.test(value) ? "info" : "neutral"; }

export function serialAsStored(value: string | null | undefined): string {
  if (value === null || value === undefined || value === "") return "No serial recorded";
  return value.trim() === value && !/[\r\n\t]/.test(value) ? value : JSON.stringify(value);
}

export function candidateLetter(index: number): string {
  let value = index + 1; let label = "";
  while (value > 0) { value--; label = String.fromCharCode(65 + value % 26) + label; value = Math.floor(value / 26); }
  return label;
}

export interface ReviewDecisionOption { decision: MigrationReviewDecision; keepSourceKey: string | null; label: string; help: string; suggested: boolean }
export function reviewDecisionOptions(detail: MigrationReviewDetail): ReviewDecisionOption[] {
  const sameItem = new Set(detail.candidates.map(candidate => candidate.itemCode)).size === 1;
  const merge = detail.kind === "same_serial_one_item" && sameItem
    ? detail.candidates.map((candidate, index) => ({
      decision: "merge_into" as const, keepSourceKey: candidate.sourceKey,
      label: `Merge into ${candidateLetter(index)}`, help: "Other rows are re-bound to this candidate.",
      suggested: detail.suggestion === "merge_into" && detail.suggestedKeepSourceKey === candidate.sourceKey,
    }))
    : detail.kind === "orphan_equipment_card" && detail.hint && sameItem
      ? detail.candidates.slice(0, 1).map((candidate, index) => ({
        decision: "merge_into" as const, keepSourceKey: candidate.sourceKey,
        label: `Attach to ${candidateLetter(index)}`, help: "Attach the orphan card to this nearest same-item candidate.",
        suggested: detail.suggestedKeepSourceKey === candidate.sourceKey,
      })) : [];
  return [...merge,
    { decision: "keep_apart", keepSourceKey: null, label: "Keep apart", help: detail.kind === "orphan_equipment_card" ? "Leave the card unattached." : "Keep these as different units.", suggested: detail.suggestion === "keep_apart" },
    { decision: "fix_in_incumbent", keepSourceKey: null, label: "Fix in the incumbent", help: "Send the issue back with a note.", suggested: detail.suggestion === "fix_in_incumbent" },
  ];
}

export function duration(milliseconds: number | null | undefined): string {
  if (milliseconds === null || milliseconds === undefined || milliseconds < 0) return "—";
  const seconds = Math.round(milliseconds / 1000); const hours = Math.floor(seconds / 3600); const minutes = Math.floor(seconds % 3600 / 60); const rest = seconds % 60;
  return [hours ? `${hours} h` : "", minutes ? `${minutes} m` : "", rest || (!hours && !minutes) ? `${rest} s` : ""].filter(Boolean).join(" ");
}
export function elapsed(start: string | null, end: string | null): string { return start && end ? duration(new Date(end).getTime() - new Date(start).getTime()) : "—"; }
export function signedChange(value: number | null): string { return value === null ? "—" : value > 0 ? `+${value.toLocaleString()}` : value < 0 ? `−${Math.abs(value).toLocaleString()}` : "0"; }
export function batchLabel(batch: Pick<MigrationBatch, "requestedAt">, latest = false): string { return latest ? "Latest extraction" : `Extraction ${new Intl.DateTimeFormat(undefined, { dateStyle: "medium", timeStyle: "short" }).format(asUtc(batch.requestedAt))}`; }
export function batchTotals(tables: readonly MigrationBatchTable[]) { return tables.reduce((sum, row) => ({ rowsRead: sum.rowsRead + row.rowsRead, inserted: sum.inserted + row.inserted, updated: sum.updated + row.updated, unchanged: sum.unchanged + row.unchanged, absent: sum.absent + row.absent, durationMilliseconds: sum.durationMilliseconds + row.durationMilliseconds }), { rowsRead: 0, inserted: 0, updated: 0, unchanged: 0, absent: 0, durationMilliseconds: 0 }); }
export function batchResult(batch: MigrationBatch): string {
  const failure = failureLabel(batch.failure); if (failure) return failure;
  if (!batch.tables?.length) return batch.status === "succeeded" ? "Full extraction completed" : batchStatusLabel(batch.status);
  const totals = batchTotals(batch.tables); return `${totals.rowsRead.toLocaleString()} rows · ${totals.inserted.toLocaleString()} new · ${totals.updated.toLocaleString()} changed · ${totals.absent.toLocaleString()} gone`;
}
export function profileTotals(profile: MigrationProfile | null) {
  if (!profile) return { flagged: 0, previous: null as number | null, outcomes: Object.fromEntries(OUTCOMES.map(x => [x, 0])) as Record<MigrationOutcome, number> };
  const counted = profile.classes.filter(row => row.count !== null);
  const flagged = counted.reduce((sum, row) => sum + row.count!, 0);
  const previous = counted.every(row => row.change !== null) ? counted.reduce((sum, row) => sum + row.count! - row.change!, 0) : null;
  const outcomes = Object.fromEntries(OUTCOMES.map(outcome => [outcome, counted.filter(row => row.outcome === outcome).reduce((sum, row) => sum + row.count!, 0)])) as Record<MigrationOutcome, number>;
  return { flagged, previous, outcomes };
}
export function sampleExample(sample: MigrationProfileSample | undefined): string {
  if (!sample) return "No sample row";
  const facts = Object.entries(sample.facts).filter(([, value]) => value !== null).slice(0, 2).map(([key, value]) => `${factLabel(key)}: ${factValue(value)}`);
  return [sample.serial, sample.item, ...facts].filter(Boolean).join(" · ");
}
export function sampleFacts(sample: MigrationProfileSample): [string, string][] { return Object.entries(sample.facts).map(([key, value]) => [factLabel(key), factValue(value)]); }

function readable(value: string): string { const text = value.replace(/_/g, " "); return text.charAt(0).toUpperCase() + text.slice(1); }
function readableValue(value: string): string { return /^[a-z][a-z0-9_]*$/.test(value) ? readable(value) : value; }
function asUtc(value: string): Date { return new Date(/(?:Z|[+-]\d{2}:\d{2})$/i.test(value) ? value : `${value}Z`); }
