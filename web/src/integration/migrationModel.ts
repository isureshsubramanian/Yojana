import type { Tone } from "../components/types";
import type {
  MigrationApply, MigrationBatch, MigrationBatchTable, MigrationOutcome, MigrationProfile, MigrationProfileSample,
  MigrationLoadAction, MigrationLoadKind, MigrationLoadSummary,
  MigrationReviewDecision, MigrationReviewDetail, MigrationReviewKind, MigrationReviewStatus,
  MigrationValidation, TemplateBlockResolution, TemplateMappingState, TemplateSourceTerms,
  ValidationResult, ValidationSignatureRole,
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
  migration_validation_failed: "Validation failed.",
  migration_apply_cancelled: "The load was cancelled.",
  migration_apply_failed: "The load failed.",
};

export const REVIEW_KINDS: readonly MigrationReviewKind[] = ["same_serial_one_item", "same_serial_different_items", "orphan_equipment_card", "serial_in_user_field", "history_needs_review"];
export const REVIEW_STATUSES: readonly MigrationReviewStatus[] = ["waiting", "decided", "carried_over"];
export function mayReadMigration(roles: readonly string[]): boolean { return roles.some(role => role === "tenant_administrator" || role === "migration_lead"); }
export function mayAdministerMigration(roles: readonly string[]): boolean { return roles.includes("tenant_administrator"); }
export function mayPrepareValidation(roles: readonly string[]): boolean { return mayReadMigration(roles); }
export function mayReviewValidation(roles: readonly string[]): boolean { return mayAdministerMigration(roles); }

export const TEMPLATE_STATES: readonly TemplateMappingState[] = ["waiting", "needs_person", "approved", "waiting_for_policy_publication"];
const templateStateLabels: Record<TemplateMappingState, string> = {
  waiting: "Waiting", needs_person: "Needs a person", approved: "Approved",
  waiting_for_policy_publication: "Waiting for policy publication",
};
const templateResolutionLabels: Record<TemplateBlockResolution, string> = {
  existing_policy: "Published policy", policy_to_create: "Create a new policy from these terms",
};

const reviewKindLabels: Record<MigrationReviewKind, string> = {
  same_serial_one_item: "Same serial on one item",
  same_serial_different_items: "Same serial on different items",
  orphan_equipment_card: "Orphan equipment card",
  serial_in_user_field: "Serial in a user field",
  history_needs_review: "History needs review",
};
const reviewStatusLabels: Record<MigrationReviewStatus, string> = { waiting: "Waiting", decided: "Decided", carried_over: "Carried over" };
const reviewDecisionLabels: Record<MigrationReviewDecision, string> = { merge_into: "Merge into", keep_apart: "Keep apart", fix_in_incumbent: "Fix in the incumbent", accept_as_recorded: "Accept as recorded", exclude_from_history: "Exclude from history" };
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
  template_mapping_needs_review: "A person must resolve this template",
  template_pattern_not_draft: "The pattern cannot be approved together",
  template_policy_unresolved: "A published policy must be selected",
  template_policy_terms_incomplete: "The source terms are incomplete",
  template_mapping_changed: "The template changed",
  template_mapping_immutable: "The approved template cannot be edited",
  template_blocks_invalid: "Cover blocks are not valid",
  template_base_family_required: "One base warranty is required",
  template_family_unknown: "Coverage family is not available",
  template_policy_not_published: "Policy is not published",
  template_policy_family_mismatch: "Policy belongs to another family",
  template_state_invalid: "Template state is not available",
  template_cursor_invalid: "Template page is no longer available",
  template_keyword_family_unknown: "Keyword family is not available",
  template_keyword_rules_empty: "At least one keyword rule is required",
  template_keyword_rule_too_long: "Keyword rule is too long",
  template_keyword_duplicate: "Each keyword can appear only once",
  migration_validation_already_running: "Validation is already running",
  migration_validation_failed: "Validation failed",
  migration_validation_map_missing: "Mapping has not run",
  migration_validation_map_failed: "The latest mapping failed",
  migration_validation_map_superseded: "A newer mapping must finish",
  migration_validation_map_not_completed: "The latest mapping has not completed",
  migration_validation_check_not_found: "Validation check not found",
  migration_validation_hard_fail_not_explainable: "A hard fail cannot be explained",
  migration_validation_check_not_failed: "Only a failed check can be explained",
  migration_validation_explanation_required: "Explanation required",
  migration_validation_signature_role_invalid: "Signature role is not valid",
  migration_validation_preparer_role_required: "Migration lead or tenant administrator access is required",
  migration_validation_reviewer_role_required: "Tenant administrator access is required",
  migration_validation_hard_fail_not_signable: "A hard fail cannot be signed",
  migration_validation_role_already_signed: "This signature step is already complete",
  migration_validation_distinct_signers_required: "The preparer and reviewer must be different people",
  migration_validation_sample_outcome_invalid: "Sample outcome is not valid",
  migration_validation_sample_not_found: "Sample not found",
  migration_validation_cursor_invalid: "Validation page is no longer available",
  migration_validation_run_not_found: "Validation run not found",
  migration_validation_not_completed: "Validation has not completed",
  migration_validation_tolerance_invalid: "History tolerance is not valid",
  migration_validation_sample_size_invalid: "Sample size is not valid",
  migration_validation_age_bands_invalid: "Age bands are not valid",
  migration_signoff_already_exists: "A sign-off record already exists",
  migration_signoff_hard_fail: "A hard fail blocks final sign-off",
  migration_signoff_unsigned_checks: "Every check needs both signatures",
  migration_signoff_not_found: "Sign-off record not found",
  migration_apply_map_not_found: "A completed load plan is required",
  migration_apply_signoff_required: "A final sign-off record is required for this load plan",
  migration_apply_load_open: "An earlier load is still open",
  migration_apply_load_not_found: "Load not found",
  migration_apply_final_load: "A final load cannot be rolled back here",
  migration_apply_not_rollbackable: "This load cannot be rolled back",
  migration_apply_connection_unconfigured: "The source connection is not configured for loading",
};

const loadStatusLabels: Record<string, string> = {
  queued: "Queued", running: "Running", completed: "Completed", failed: "Failed", rolled_back: "Rolled back",
};
const rollbackOutcomeLabels: Record<string, string> = {
  rolled_back: "Rolled back", refused_touched: "Refused because units were touched", nothing_to_remove: "Nothing to remove",
};
const refusalReasonLabels: Record<string, string> = {
  item_not_synced: "Item has not synced",
  item_unknown: "Item is not known",
  item_not_serial_tracked: "Item is not serial tracked",
  already_open_on_another_unit: "Identifier is already open on another unit",
  duplicate_in_batch: "Identifier is duplicated in this load",
  replacement_unit_not_loaded: "Replacement unit was not loaded",
  entitlement_policy_unknown: "Cover policy is not known",
  entitlement_policy_not_published: "Cover policy is not published",
  entitlement_policy_version_differs: "Cover policy version differs",
  entitlement_policy_family_differs: "Cover policy belongs to another family",
  entitlement_direction_unknown: "Cover direction is not known",
  entitlement_start_required: "Cover start date is required",
  entitlement_dates_invalid: "Cover dates are not valid",
  entitlement_termination_before_start: "Cover ends before it starts",
  entitlement_evidence_required: "Cover evidence is required",
  entitlement_selected_evidence_required: "Selected cover evidence is required",
  entitlement_selected_evidence_differs: "Selected cover evidence differs",
  family_occupied: "Coverage family already has cover",
  migration_certificate_version_not_one: "Migrated certificate must be version one",
  migration_certificate_provenance_invalid: "Certificate provenance is not valid",
  migration_certificate_unknowns_required: "Certificate unknowns are required",
  migration_certificate_evidence_required: "Certificate evidence is required",
  migration_certificate_required: "Migrated certificate is required",
  replacement_link_invalid: "Replacement link is not valid",
  replacement_policy_must_be_unknown: "Replacement policy must be unknown",
  identifier_required: "An identifier is required",
  history_depth_unknown: "History depth is not known",
  condition_grade_unknown: "Condition grade is not known",
  ownership_party_is_us: "The tenant cannot be recorded as the customer",
  identifier_type_repeated: "Identifier type is repeated",
  empty: "Identifier is empty",
  asterisk_not_encodable_in_code39: "Asterisk cannot be encoded in Code 39",
};

const validationCheckLabels: Record<string, string> = {
  unit_master: "Unit master count",
  active_cover: "Active cover at cutover",
  open_service_calls: "Open service calls",
  stock_by_item_warehouse: "In stock per item and warehouse",
  serial_in_multiple_warehouses: "Serial in two warehouses",
  negative_quantity: "Negative quantity",
  historical_events: "Historical events",
  status_histogram: "Status histogram",
  event_per_unit: "At least one event per unit",
  one_current_row: "Exactly one current row per unit",
  signed_quantity: "Signed quantity per serial",
  stratified_sample: "Stratified sample",
  cover_gap_report: "Units sold with no base cover",
  attachments_by_hash: "Attachments by content hash",
};
const validationResultLabels: Record<ValidationResult, string> = {
  pass: "Pass", explained: "Explained", fail: "Fail", hard_fail: "Hard fail", report: "Report",
  warning: "Warning",
};
const validationRoleLabels: Record<ValidationSignatureRole, string> = { preparer: "Prepared", reviewer: "Reviewed" };
const validationDifferenceLabels: Record<string, string> = {
  item_warehouse: "Item and warehouse", status_bucket: "Status bucket", serial_quantity: "Serial quantity",
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
export function validationCheckLabel(code: string, supplied?: string): string { return validationCheckLabels[code] ?? (supplied?.trim() || readable(code)); }
export function validationResultLabel(value: ValidationResult): string { return validationResultLabels[value]; }
export function validationResultTone(value: ValidationResult): Tone { return value === "pass" ? "ok" : value === "explained" || value === "warning" ? "warn" : value === "fail" || value === "hard_fail" ? "bad" : "info"; }
export function validationLimitLabel(value: string): string {
  if (value === "zero") return "Zero";
  if (value === "none") return "None";
  if (value === "all") return "All";
  const percent = /^<\s*([\d.]+)%$/.exec(value);
  return percent ? `Less than ${percent[1]}%` : readable(value);
}
export function validationRoleLabel(value: ValidationSignatureRole): string { return validationRoleLabels[value]; }
export function validationDifferenceLabel(value: string): string { return validationDifferenceLabels[value] ?? readable(value); }
export function validationAttentionCount(run: MigrationValidation | null | undefined): number {
  return run?.checks.filter(check => check.signatures.length < 2 || check.result === "fail" || check.result === "hard_fail").length ?? 0;
}
export function signoffDisabledReason(run: MigrationValidation | null | undefined): string | null {
  if (!run) return "Run validation checks first";
  if (run.signoffRecordId) return "A sign-off record exists for this run";
  if (run.status !== "completed") return "Validation checks have not completed";
  if (run.rehearsal) return null;
  const reasons: string[] = [];
  if (run.hardFailed) reasons.push(`${run.hardFailed.toLocaleString()} hard fail${run.hardFailed === 1 ? "" : "s"}`);
  const unsigned = Math.max(0, run.checksTotal - run.signedChecks);
  if (unsigned) reasons.push(`${unsigned.toLocaleString()} check${unsigned === 1 ? " is" : "s are"} not signed`);
  return reasons.length ? reasons.join(" and ") : null;
}
export function finalLoadDisabledReason(run: MigrationValidation | null | undefined): string | null {
  if (!run) return "Run final validation first";
  if (run.rehearsal) return "The latest validation run is a rehearsal; run final validation";
  if (run.status !== "completed") return run.status === "failed" ? "Final validation failed" : "Final validation has not completed";
  return run.hasFinalSignoff ? null : "Create the final sign-off record for this load plan";
}
export function phaseStatusLabel(value: string | null | undefined): string {
  if (!value || value === "not_run") return "Not run";
  if (/^(queued|waiting)$/.test(value)) return "Waiting";
  if (value === "running") return "Running";
  if (/^(completed|succeeded)$/.test(value)) return "Done";
  if (/^(failed|refused|blocked)$/.test(value)) return "Failed";
  return readable(value);
}
export function phaseStatusTone(value: string | null | undefined): Tone {
  const label = phaseStatusLabel(value);
  return label === "Done" ? "ok" : label === "Running" ? "info" : label === "Failed" ? "bad" : "neutral";
}
export function loadStatusLabel(value: string): string { return loadStatusLabels[value] ?? readable(value); }
export function loadStatusTone(value: string): Tone { return value === "completed" ? "ok" : value === "failed" ? "bad" : /^(queued|running)$/.test(value) ? "info" : "neutral"; }
export function loadKindLabel(value: MigrationLoadKind): string { return value === "rehearsal" ? "Rehearsal" : value === "final" ? "Final" : "Delta"; }
export function loadKindTone(value: MigrationLoadKind): Tone { return value === "final" ? "ok" : value === "delta" ? "info" : "neutral"; }
export function migrationApplyKind(load: Pick<MigrationApply, "label" | "rehearsal">): MigrationLoadKind { return (load.label ?? "").startsWith("D") ? "delta" : load.rehearsal ? "rehearsal" : "final"; }
export function deltaKindTone(value: string): Tone {
  return value === "new_unit" ? "ok" : value === "cover_replaced" ? "info"
    : value === "cover_refused" || value === "owner_differs" ? "warn" : "neutral";
}
export function loadActionState(action: MigrationLoadAction | null | undefined, busy = false): { disabled: boolean; reasons: string[] } {
  if (busy) return { disabled: true, reasons: ["Another load action is running"] };
  if (!action) return { disabled: true, reasons: ["Checking load readiness"] };
  return { disabled: !action.allowed || action.reasons.length > 0, reasons: action.reasons.map(reason => reason.message) };
}
export function migrationTimestamp(value: string): string {
  const timestamp = new Date(/(?:Z|[+-]\d{2}:\d{2})$/i.test(value) ? value : `${value}Z`);
  return new Intl.DateTimeFormat(undefined, {
    year: "numeric", month: "short", day: "2-digit", hour: "2-digit", minute: "2-digit", second: "2-digit",
  }).format(timestamp);
}
export function handOverSentence(
  load: Pick<MigrationLoadSummary, "batchDisplayNumber" | "handOver">,
  format: (value: string) => string = migrationTimestamp,
): string | null {
  const handOver = load.handOver;
  if (!handOver) return null;
  if (handOver.note) return handOver.note;
  const from = handOver.from ? format(handOver.from) : "the retained polling position";
  const lead = `Sync hand-over: ${handOver.connectionName} will read source changes from ${from} (source clock when ${load.batchDisplayNumber} started).`;
  return handOver.syncState === "running" ? `${lead} The connection is running.` : `${lead} The connection is stopped — Resume the sync.`;
}
export function deltaExportUrl(loadId: string, format: "csv" | "xlsx", filters: { kind?: string; itemCode?: string; serial?: string }): string {
  const query = new URLSearchParams();
  for (const [key, value] of Object.entries(filters)) if (value) query.set(key, value);
  const suffix = query.toString();
  return `/api/migration/loads/${loadId}/delta/export.${format}${suffix ? `?${suffix}` : ""}`;
}
export function undoConfirmation(load: Pick<MigrationLoadSummary, "kind" | "label">): { title: string; detail: string } {
  if (load.kind === "final") return { title: `Undo ${load.label}?`, detail: "The module goes back to before the final load and the connection's polling position is restored." };
  if (load.kind === "delta") return { title: `Undo ${load.label}?`, detail: "The units and facts this delta wrote will be removed, and the migrated cover it replaced will be restored." };
  return { title: `Undo ${load.label}?`, detail: "Every unit, fact, cover row, link and certificate this rehearsal wrote will be removed." };
}
export function rollbackOutcomeLabel(value: string): string { return rollbackOutcomeLabels[value] ?? readable(value); }
export function refusalReasonLabel(value: string): string {
  const separator = value.indexOf(":");
  if (separator < 0) return refusalReasonLabels[value] ?? readable(value);
  const scope = value.slice(0, separator); const reason = value.slice(separator + 1);
  return `${readable(scope)}: ${refusalReasonLabels[reason] ?? readable(reason)}`;
}
export function countTotal(rows: readonly { count: number }[]): number { return rows.reduce((sum, row) => sum + row.count, 0); }
export function compactNumber(value: number): string {
  return new Intl.NumberFormat(undefined, { notation: Math.abs(value) >= 1_000_000 ? "compact" : "standard", maximumFractionDigits: 2 }).format(value);
}
export function percentage(value: number | null): string { return value === null ? "—" : `${value.toLocaleString(undefined, { maximumFractionDigits: 2 })}%`; }
export function validationTrail(json: string): Record<string, unknown>[] {
  try { const parsed: unknown = JSON.parse(json); return Array.isArray(parsed) ? parsed.filter(value => value !== null && typeof value === "object") as Record<string, unknown>[] : []; }
  catch { return []; }
}
export function validationTrailLine(event: Record<string, unknown>): string {
  const value = (camel: string, pascal: string) => event[camel] ?? event[pascal];
  const movement = value("movementKind", "MovementKind");
  const date = value("effectiveDate", "EffectiveDate");
  const document = value("documentNumber", "DocumentNumber");
  const quantity = value("quantity", "Quantity");
  const warehouse = value("warehouseCode", "WarehouseCode") ?? value("warehouse", "Warehouse");
  return [date && String(date).slice(0, 10), movement && readable(String(movement)), document != null && `document ${String(document)}`, quantity != null && `quantity ${Number(quantity).toLocaleString()}`, warehouse && `warehouse ${String(warehouse)}`].filter(Boolean).join(" · ") || "Event details unavailable";
}
export function statusTone(value: string): Tone { return /^(succeeded|completed)$/.test(value) ? "ok" : /^(failed|refused|blocked)$/.test(value) ? "bad" : /^(queued|running)$/.test(value) ? "info" : "neutral"; }
export function templateStateLabel(value: TemplateMappingState): string { return templateStateLabels[value]; }
export function templateStateTone(value: TemplateMappingState): Tone { return value === "approved" ? "ok" : value === "needs_person" ? "warn" : "info"; }
export function templateResolutionLabel(value: TemplateBlockResolution): string { return templateResolutionLabels[value]; }
export function templateFamilyLabel(code: string, supplied?: string): string { return supplied?.trim() || readable(code); }

export function templateTermsLabel(terms: TemplateSourceTerms): string {
  const duration = terms.durationValue === null ? "Duration not recorded" : `${terms.durationValue.toLocaleString()} ${terms.durationUnit}${terms.durationUnitVerified ? "" : " (assumed)"}`;
  const cover = [["parts", terms.partsPct], ["labour", terms.labourPct], ["travel", terms.travelPct]] as const;
  const inclusions = cover.map(([name, value]) => value === null ? `${name} not recorded` : value === 100 ? `${name} included` : value === 0 ? `${name} excluded` : `${name} ${value.toLocaleString()}%`).join(", ");
  const response = terms.responseHours === null ? null : terms.responseSource
    ? `response ${terms.responseSource}${terms.responseSource.toLowerCase() === `${terms.responseHours} hours` ? "" : ` (read as ${terms.responseHours.toLocaleString()} hours)`}`
    : `response ${terms.responseHours.toLocaleString()} hours`;
  return [duration, inclusions, response, coverageHoursLabel(terms.coverageHours)].filter(Boolean).join(" · ");
}

export function coverageHoursLabel(value: string | null): string | null {
  if (!value) return null;
  const days: Record<string, string> = { mon: "Mon", tue: "Tue", wed: "Wed", thu: "Thu", fri: "Fri", sat: "Sat", sun: "Sun" };
  const clock = (part: string) => /^\d{4}$/.test(part) ? `${part.slice(0, 2)}:${part.slice(2)}` : part;
  return value.split(";").map(entry => {
    const [day, range] = entry.split("=", 2); const [start, end] = (range ?? "").split("-", 2);
    return `${days[day ?? ""] ?? readable(day ?? "day")} ${clock(start ?? "")}–${clock(end ?? "")}`;
  }).join(", ");
}

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
  if (detail.kind === "history_needs_review") return [
    { decision: "accept_as_recorded", keepSourceKey: null, label: "Accept as recorded", help: "Keep the flagged source events in history.", suggested: detail.suggestion === "accept_as_recorded" },
    { decision: "exclude_from_history", keepSourceKey: null, label: "Exclude from history", help: "Drop only the flagged events; keep the rest of this serial's history.", suggested: detail.suggestion === "exclude_from_history" },
    { decision: "fix_in_incumbent", keepSourceKey: null, label: "Fix in the incumbent", help: "Send the issue back with a note.", suggested: detail.suggestion === "fix_in_incumbent" },
  ];
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
export function batchLabel(batch: Pick<MigrationBatch, "displayNumber">, latest = false): string { return latest ? `Latest extraction · ${batch.displayNumber}` : `Extraction ${batch.displayNumber}`; }
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

function readable(value: string): string { const text = value.replace(/_/g, " ").replace(/([a-z0-9])([A-Z])/g, (_, a: string, b: string) => `${a} ${b.toLowerCase()}`); return text.charAt(0).toUpperCase() + text.slice(1); }
function readableValue(value: string): string { return /^[a-z][a-z0-9_]*$/.test(value) ? readable(value) : value; }
function asUtc(value: string): Date { return new Date(/(?:Z|[+-]\d{2}:\d{2})$/i.test(value) ? value : `${value}Z`); }
