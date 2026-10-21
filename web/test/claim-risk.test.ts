import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { createRequire } from "node:module";
import test from "node:test";
import { fileURLToPath } from "node:url";
import { build } from "esbuild";
import { createElement, type ComponentType } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { createMemoryRouter, RouterProvider } from "react-router";
import { api, ApiError } from "../src/api/client.ts";
import {
  claimRiskDecisionLabel, claimRiskEvidence, claimRiskRuleLabel, claimRiskThresholdLabel,
} from "../src/claims/format.ts";
import type { ClaimRiskRulesPolicy } from "../src/claims/types.ts";

async function compile<T = Record<string, ComponentType<any>>>(path: string) {
  const result = await build({ entryPoints: [fileURLToPath(new URL(path, import.meta.url))], bundle: true, platform: "node", format: "cjs", jsx: "automatic", packages: "external", loader: { ".css": "empty" }, write: false });
  const module = { exports: {} as T };
  new Function("require", "module", "exports", result.outputFiles[0]!.text)(createRequire(import.meta.url), module, module.exports);
  return module.exports;
}

const ui = await compile<typeof import("../src/claims/ClaimRiskUi.tsx")>("../src/claims/ClaimRiskUi.tsx");
const source = (path: string) => readFileSync(new URL(`../src/${path}`, import.meta.url), "utf8");

const policy: ClaimRiskRulesPolicy = {
  recentCoverPurchase: { enabled: true, days: 60 }, repeatUnitClaims: { enabled: true, minimumEarlierClaims: 2, days: 90 },
  repeatFault: { enabled: true, days: 180 }, claimantRate: { enabled: true, days: 365, rateMultiplier: 3, minimumClaims: 5, minimumUnits: 20 },
  channelMismatch: { enabled: true }, recentOwnershipChange: { enabled: true, days: 30 }, partNotReturned: { enabled: true },
  duplicateOpenClaim: { enabled: true }, reviewerRole: "service_manager", requireDifferentReviewer: true,
};

test("the eight claim-risk codes and final decisions have readable catalogue labels", () => {
  assert.deepEqual([
    "recent_cover_purchase", "repeat_unit_claims", "repeat_fault", "claimant_rate", "channel_mismatch",
    "recent_ownership_change", "part_not_returned", "duplicate_open_claim",
  ].map(claimRiskRuleLabel), [
    "Cover bought just before", "Repeat claims on the unit", "Same fault again", "High claim rate",
    "Not the owner or seller", "Owner just changed", "Part not returned", "Already on another open claim",
  ]);
  assert.equal(claimRiskDecisionLabel("cleared"), "Cleared");
  assert.equal(claimRiskDecisionLabel("confirmed"), "Concern confirmed");
  assert.match(claimRiskThresholdLabel("claimant_rate", policy), /3× tenant rate.*365 days.*5 claims.*20 units/);
});

test("every rule renders stored evidence as sentences without dumping unknown JSON", () => {
  const cases: [string, Record<string, unknown>, Record<string, unknown>, RegExp][] = [
    ["recent_cover_purchase", { incident_date: "2026-09-01", lines: [{ coverageFamily: "accidental_damage", purchaseDate: "2026-07-21", daysAfterPurchase: 42 }] }, { days: 60 }, /Accidental damage.*42 days later.*60 days/],
    ["repeat_unit_claims", { incident_date: "2026-09-01", earlier_claims: [{ claimNumber: "CLM-2026-01104", incidentDate: "2026-08-08", status: "approved" }, { claimNumber: "CLM-2026-01260", incidentDate: "2026-08-19", status: "approved" }] }, { minimum_earlier_claims: 2, days: 90 }, /CLM-2026-01104.*CLM-2026-01260.*2 within 90 days/],
    ["repeat_fault", { reported_cause_code: "power_supply_failure", earlier_claims: [{ claimNumber: "CLM-2026-01077", incidentDate: "2026-07-03" }] }, { days: 180 }, /power supply failure.*CLM-2026-01077.*180 days/],
    ["claimant_rate", { party_claims: 6, party_units: 20, party_rate: 0.3, tenant_claims: 10, tenant_units: 1000, tenant_rate: 0.01 }, { days: 365, rate_multiplier: 3 }, /6 claims across 20 units.*30%.*tenant baseline.*1%.*30×.*3×/],
    ["channel_mismatch", { claimant_party_ref: "11111111-1111-1111-1111-111111111111", hidden_key: "do-not-render" }, {}, /not the recorded owner or custodian/],
    ["recent_ownership_change", { incident_date: "2026-09-01", ownership: { openedAt: "2026-08-10", partyRef: "22222222-2222-2222-2222-222222222222" } }, { days: 30 }, /2026-08-10.*22 days before.*30 days/],
    ["part_not_returned", { outstanding_returns: [{ part_serial: "NVME-2608-0042", sourceNumber: "CLM-2026-00981", returnDue: "2026-08-28", replacementLinkId: "hidden" }] }, { overdue_before_incident: true }, /NVME-2608-0042.*CLM-2026-00981.*2026-08-28/],
    ["duplicate_open_claim", { claims: [{ claimNumber: "CLM-2026-01391", status: "adjudicating", serials: [{ kind: "part", serial: "SSD-2607-0088", unit_id: "hidden" }] }] }, { open_statuses: ["submitted"] }, /CLM-2026-01391.*adjudicating.*part SSD-2607-0088/],
  ];
  for (const [rule, evidence, thresholds, expected] of cases) {
    const text = claimRiskEvidence(rule, evidence, thresholds).join(" ");
    assert.match(text, expected, rule);
    assert.doesNotMatch(text, /11111111|22222222|hidden_key|do-not-render|replacementLinkId|unit_id|\{\"/i, rule);
  }
});

test("the HTTP client preserves open rule codes from an adjudication hold", async () => {
  const original = globalThis.fetch;
  globalThis.fetch = async () => new Response(JSON.stringify({ title: "claim_risk_review_pending", detail: "Approval is held.", rule: "claim_risk_review_pending", openRules: ["repeat_fault", "channel_mismatch"] }), { status: 409, headers: { "content-type": "application/problem+json" } });
  try {
    await assert.rejects(api("/api/claims/hidden/adjudicate", { method: "POST", body: {} }), (reason: unknown) => {
      assert.ok(reason instanceof ApiError); assert.equal(reason.rule, "claim_risk_review_pending");
      assert.deepEqual(reason.openRules, ["repeat_fault", "channel_mismatch"]); return true;
    });
  } finally { globalThis.fetch = original; }
});

test("risk chips and the adjudication hold stay text-led and link to the requested claim drawer", () => {
  const open = renderToStaticMarkup(createElement(ui.ClaimRiskFlagChip, { flag: { ruleCode: "repeat_fault", status: "open" } }));
  const decided = renderToStaticMarkup(createElement(ui.ClaimRiskFlagChip, { flag: { ruleCode: "part_not_returned", status: "cleared" } }));
  assert.match(open, /Same fault again/); assert.match(open, /class="claim-risk-chip open"/);
  assert.match(decided, /Part not returned · cleared/); assert.match(decided, /class="claim-risk-chip decided"/);
  const router = createMemoryRouter([{ path: "*", element: createElement(ui.ClaimRiskHoldNotice, { claimId: "hidden-claim", rules: ["repeat_fault", "channel_mismatch"] }) }], { initialEntries: ["/claims/fixture"] });
  try {
    const held = renderToStaticMarkup(createElement(RouterProvider, { router }));
    assert.match(held, /Approval held for risk review/); assert.match(held, /Same fault again, Not the owner or seller/);
    assert.match(held, /\/claims\/risk-review\?claim=hidden-claim/);
  } finally { router.dispose(); }
});

test("the risk-review screen uses the approved filters, paged grid, report and per-flag decision contract", () => {
  const screen = source("claims/ClaimRiskReview.tsx");
  const api = source("claims/api.ts");
  const routes = source("routes.tsx");
  const claims = source("claims/Claims.tsx");
  for (const text of ["Risk review", "Held claims", "How the rules are doing", "Open flags", "Decided", "Last 30 days", "Last 90 days"]) assert.ok(screen.includes(text), text);
  assert.match(screen, /<DataGrid/); assert.match(screen, /nextCursor/); assert.match(screen, /keyset paged/);
  assert.match(screen, /useReferenceNames/); assert.match(screen, /claimsApi\.detail/); assert.match(screen, /claimsApi\.claimRiskFlags/);
  assert.match(screen, /for \(const flag of open\)/); assert.match(screen, /claimsApi\.decideRiskFlag/); assert.match(screen, /reason\.trim\(\)/);
  assert.match(api, /\/api\/claims\/risk-flags/); assert.match(api, /risk-flags\/report/); assert.match(api, /risk-rules/);
  assert.match(routes, /claims\/risk-review/); assert.match(claims, /<ClaimsTabs current="claims"/);
});

test("claim adjudication and Settings wire the risk hold and administrator-only rule editor", () => {
  const adjudication = source("claims/ClaimAdjudication.tsx");
  const settings = source("settings/ClaimRiskRules.tsx");
  const catalogue = source("settings/Settings.tsx");
  assert.match(adjudication, /openRiskFlags/); assert.match(adjudication, /ClaimRiskHoldNotice/);
  assert.match(adjudication, /claim_risk_review_pending/); assert.match(adjudication, /reason\.openRules/);
  assert.match(adjudication, /Deny…/); assert.match(adjudication, /heldRules\.length > 0/);
  assert.match(catalogue, /Claims risk rules/); assert.match(catalogue, /\/settings\/claim-risk/);
  for (const field of ["recentCoverPurchase", "repeatUnitClaims", "repeatFault", "claimantRate", "channelMismatch", "recentOwnershipChange", "partNotReturned", "duplicateOpenClaim", "reviewerRole", "requireDifferentReviewer"]) assert.ok(settings.includes(field), field);
  assert.match(settings, /tenant_administrator/); assert.match(settings, /Read only/); assert.match(settings, /STAFF_ROLES/);
  assert.match(settings, /claimsApi\.saveRiskRules/); assert.match(settings, /<ErrorNote error=\{error\}/);
});
