import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import { blockerPath, canConfirm, erasureStatus, erasureSteps } from "../src/settings/privacyModel.ts";
import { privacyApi, type ErasureDetail, type ErasurePreview } from "../src/settings/privacyApi.ts";

const source = (path: string) => readFileSync(new URL(`../src/${path}`, import.meta.url), "utf8");
const list = source("settings/Privacy.tsx");
const form = source("settings/PrivacyNew.tsx");
const registry = source("settings/registry.ts");
const routes = source("routes.tsx");

test("privacy list keeps the vetted KPI, paged log, CSV action and drawer states", () => {
  for (const label of ["Completed", "In progress", "Refused", "Oldest open request", "Export log", "New erasure", "Erasure requests"])
    assert.ok(list.includes(label), label);
  for (const token of ["<DataGrid", "nextCursor", "openDrawer", "Per-store counts", "Kept, and why", "requestReceivedOn"])
    assert.ok(list.includes(token), token);
  assert.match(list, /if \(row\.status !== "refused"\) return row\.token/);
  assert.match(list, /parties\.party\(row\.subjectId\)/);
  assert.match(list, /people\(row\.subjectId\)/);
});

test("new erasure keeps all three steps, existing pickers, gating and mismatch feedback", () => {
  for (const label of ["1 · Who and why", "2 · Preview", "3 · Confirm", "This customer is an individual", "Request received", "Free text to review", "Erase personal data", "Request reference does not match."])
    assert.ok(form.includes(label), label);
  assert.match(form, /<CustomerField/);
  assert.match(form, /<UserPicker/);
  assert.match(form, /<FormActions>/);
  assert.match(form, /className="btn danger"/);
  assert.match(form, /<Icon name="shield" \/>/);
  assert.match(form, /disabled=\{busy \|\| !canConfirm/);
});

test("review choices and typed reference both gate confirmation", () => {
  const preview: ErasurePreview = {
    subjectKind: "Party", subjectId: "person-1", token: "Erased customer · 0001", counts: [], accuracyNote: "review",
    reviewItems: [
      { itemKey: "claim:1", store: "serialisation", recordReference: "CLM-1043", field: "description", excerpt: "text" },
      { itemKey: "rma:1", store: "serialisation", recordReference: "RMA-0381", field: "note", excerpt: "text" },
    ],
  };
  assert.equal(canConfirm(null, {}, "DSR-1", "DSR-1"), false);
  assert.equal(canConfirm(preview, { "claim:1": "Redact" }, "DSR-1", "DSR-1"), false);
  assert.equal(canConfirm(preview, { "claim:1": "Redact", "rma:1": "Keep" }, "DSR-1", "DSR-2"), false);
  assert.equal(canConfirm(preview, { "claim:1": "Redact", "rma:1": "Keep" }, "DSR-1", "DSR-1"), true);
});

test("refused state links only to supported opaque record routes", () => {
  assert.equal(blockerPath({ kind: "claim", recordId: "claim-id", reference: "CLM-1", status: "open", detail: "Open" }), "/claims/claim-id");
  assert.equal(blockerPath({ kind: "rma", recordId: "rma-id", reference: "RMA-1", status: "open", detail: "Open" }), "/returns/rma-id");
  assert.equal(blockerPath({ kind: "replacement", recordId: "replacement-id", reference: "REP-1", status: "open", detail: "Open" }), "/replacements/replacement-id");
  assert.equal(blockerPath({ kind: "replacement_hold", recordId: null, reference: "Hold", status: "open", detail: "Open" }), null);
  assert.equal(erasureStatus("refused", "erasure_open_obligations").label, "Refused · open business");
  for (const label of ["Can't erase yet.", "The request is saved as refused", "Live warranties do not block", "Preview again"])
    assert.ok(form.includes(label), label);
});

test("drawer progress derives four visible steps from narrow store counts", () => {
  const detail = {
    status: "running", failureDetail: null,
    counts: [
      { store: "platform", kind: "mail", previewCount: 2, erasedCount: 2, action: "replace", status: "completed", completedAt: "2026-09-01T10:00:00Z" },
      { store: "integration", kind: "payload", previewCount: 4, erasedCount: 0, action: "rewrite", status: "previewed", completedAt: null },
    ],
  } as ErasureDetail;
  const steps = erasureSteps(detail);
  assert.equal(steps.length, 4);
  assert.deepEqual(steps.map(step => step.status), ["Done", "Done", "Running", "Waiting"]);
});

test("privacy routes and settings entry are administrator-only in the standard not-allowed state", () => {
  assert.match(registry, /name: "Privacy"[\s\S]{0,220}path: "\/settings\/privacy"[\s\S]{0,160}roles: administrator/);
  assert.match(routes, /path: "privacy\/new", lazy: \(\) => import\("\.\/settings\/PrivacyNew"\)/);
  assert.match(list, /roles\.includes\("tenant_administrator"\)/);
  assert.match(form, /roles\.includes\("tenant_administrator"\)/);
  assert.match(list, /Tenant administrator access is required/);
  assert.match(form, /Tenant administrator access is required/);
});

test("privacy client sends the received date and uses server paging and detail", async () => {
  const calls: Array<{ path: string; body: string | null }> = [];
  const original = globalThis.fetch;
  globalThis.fetch = (async (input: string | URL | Request, init?: RequestInit) => {
    calls.push({ path: String(input), body: init?.body ? String(init.body) : null });
    if (String(input).includes("/preview")) return new Response(JSON.stringify({ subjectKind: "Party", subjectId: "id", token: "token", counts: [], reviewItems: [], accuracyNote: "note" }), { status: 200 });
    if (String(input).match(/erasures\/[^?]+$/)) return new Response(JSON.stringify({
      erasureId: "id", subjectKind: "Party", subjectId: "subject-id", token: "token", requestReference: "DSR-1", requestedBy: "Priya Raman",
      requestReceivedOn: "2026-09-01", status: "completed", requestedAt: "2026-09-02T08:00:00Z", completedAt: "2026-09-02T08:05:00Z", failureRule: null,
      reason: "request", startedAt: "2026-09-02T08:01:00Z", failedAt: null, retentionBasis: "contract", counts: [], failureDetail: null, blockers: [],
    }), { status: 200 });
    return new Response(JSON.stringify({ items: [], nextCursor: null, summary: { completedLastTwelveMonths: 0, inProgress: 0, refused: 0, oldestOpen: null } }), { status: 200 });
  }) as typeof fetch;
  try {
    await privacyApi.page("cursor value");
    await privacyApi.detail("opaque-id");
    await privacyApi.preview({ subjectKind: "Party", subjectId: "subject-id", requestReference: "DSR-1", requestReceivedOn: "2026-09-01", reason: "request", subjectIsNaturalPerson: true });
    assert.equal(calls[0]?.path, "/api/privacy/erasures?take=50&cursor=cursor+value");
    assert.equal(calls[1]?.path, "/api/privacy/erasures/opaque-id");
    assert.match(calls[2]?.body ?? "", /"requestReceivedOn":"2026-09-01"/);
  } finally { globalThis.fetch = original; }
});
