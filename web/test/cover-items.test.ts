import assert from "node:assert/strict";
import test from "node:test";
import { createRequire } from "node:module";
import { fileURLToPath } from "node:url";
import { readFileSync } from "node:fs";
import { build } from "esbuild";
import { createElement, type ComponentType } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { createMemoryRouter, RouterProvider } from "react-router";
import type { WhatsCoveredPage, ItemCover, PolicyCoverItem, ClaimGuidance, ClaimCause } from "../src/entitlements/coverItems.ts";

async function compile<T = Record<string, ComponentType<any>>>(path: string) {
  const result = await build({ entryPoints: [fileURLToPath(new URL(path, import.meta.url))], bundle: true, platform: "node", format: "cjs", jsx: "automatic", packages: "external", write: false });
  const module = { exports: {} as T };
  new Function("require", "module", "exports", result.outputFiles[0]!.text)(createRequire(import.meta.url), module, module.exports);
  return module.exports;
}
const { coverItemsApi, customerEmailEnabled, unitTab, startClaimUrl, policyCoverUrl } = await compile<typeof import("../src/entitlements/coverItems.ts")>("../src/entitlements/coverItems.ts");
function renderInRouter(element: import("react").ReactNode) {
  const session = { user: { id: "hidden-user", displayName: "Fictional Service Agent", email: "quality@example.invalid" }, tenant: { code: "fixture", name: "Fictional Preview" }, roles: [], tenants: [] };
  const router = createMemoryRouter([{ id: "root", path: "*", loader: () => session, element }], { initialEntries: ["/"], hydrationData: { loaderData: { root: session } } });
  try { return renderToStaticMarkup(createElement(RouterProvider, { router })); } finally { router.dispose(); }
}
const covered = await compile("../src/entitlements/WhatsCoveredPanel.tsx");
const causeEditor = await compile("../src/entitlements/ClaimCausesEditor.tsx");
const policy = await compile("../src/entitlements/PolicyItemsEditor.tsx");
const claim = await compile("../src/claims/ClaimCausePanel.tsx");
const tabs = await compile("../src/units/UnitTabBar.tsx");
const damage: ItemCover = { entitlementId: "hidden-damage", familyCode: "accidental_damage", familyName: "Accidental damage", policyName: "Accidental damage 24 months", policyVersion: 2, from: "2026-03-12", until: "2028-03-11", asOf: "2026-09-01", future: false, usage: [{ meterType: "claims", limit: 2, used: 0, remaining: 2, period: "term" }] };
const base: ItemCover = { ...damage, entitlementId: "hidden-base", familyCode: "base_warranty", familyName: "Base warranty", policyName: "Standard 12 months", usage: [] };
const data: WhatsCoveredPage = { unitId: "hidden-unit", serial: "RS11-2509-04417", itemName: "RS-1100 rack server", customerName: "Meridian Data Labs", asOf: "2026-09-01", covers: [base, damage, { ...base, familyName: "Extended term", future: true, from: "2026-11-25" }], coveredCount: 11, excludedCount: 7, total: 1, skip: 0, take: 7, notice: "Applicability cannot be established", items: [{ key: "cause:LIQUID_SPILL", customerWording: "Liquid spills", category: "cause", causeCodes: ["LIQUID_SPILL"], covered: true, canBeCovered: "—", grants: [{ code: "ADP-LIQ", customerWording: "Liquid spills", internalNote: "Secret service instruction", category: "cause", partsScope: { partCategories: [], itemFamilies: [] }, cover: damage }], excludes: [{ code: "EX-LIQ", customerWording: "Liquid damage", internalNote: null, category: "cause", partsScope: { partCategories: [], itemFamilies: [] }, cover: base }] }] };
const item: PolicyCoverItem = { code: "LEGACY-1", type: "excluded", customerWording: "Consumables, batteries after 12 months", internalNote: "Secret service instruction", category: null, partsScope: { partCategories: [], itemFamilies: [] }, causeCodes: [] };

test("what's covered renders customer wording, named grants, independent limits and future start", () => {
  const strip = renderToStaticMarkup(createElement(covered.CoverStrip!, { data }));
  const table = renderToStaticMarkup(createElement(covered.CoverItemsTable!, { data, covered: true }));
  for (const text of ["Base warranty", "Accidental damage", "not yet running", "2 claims left / term", "Extended term"]) assert.ok(strip.includes(text), text);
  for (const text of ["Liquid spills", "Accidental damage 24 months", "2028", "Covered by", "Until · limit"]) assert.ok(table.includes(text), text);
  assert.doesNotMatch(table + strip, /Secret service instruction|hidden-unit|hidden-damage|hidden-base/);
  const internal = renderToStaticMarkup(createElement(covered.CoverItemsTable!, { data, covered: true, internal: true }));
  assert.match(internal, /Secret service instruction/);
});

test("excluded table does not turn unknown applicability into an offer", () => {
  const no = { ...data, items: [{ ...data.items[0]!, covered: false, grants: [] }] };
  const html = renderToStaticMarkup(createElement(covered.CoverItemsTable!, { data: no, covered: false }));
  assert.match(html, /Excluded by/); assert.match(html, /Can it be covered/); assert.match(html, /Base warranty/);
  assert.doesNotMatch(html, /Yes ·|not offered/);
});

test("policy items table marks uncategorised legacy rows and stages edits using house components", () => {
  const html = renderInRouter(createElement(policy.PolicyItemsEditor!, { versionId: "hidden-version", items: [item], editable: true, dirty: true, onChange: () => {} }));
  for (const text of ["Covered and excluded items", "LEGACY-1", "Needs category", "Customer wording", "Add item", "Preview customer sheet", "Save draft", "Since"]) assert.ok(html.includes(text), text);
  assert.doesNotMatch(html, /Secret service instruction|hidden-version/);
  const drawer = renderToStaticMarkup(createElement(policy.ItemForm!, { item, causes: [{ code: "LIQUID_SPILL", customerWording: "Liquid spill", active: true }], editable: true, fresh: false, onSave: () => {}, onRemove: () => {} }));
  for (const text of ["Customer wording", "Internal note", "Claim causes it answers", "Liquid spill", "Part categories", "Item-family references", "Keep in draft"]) assert.ok(drawer.includes(text), text);
});

test("policy customer preview contains only customer wording", () => {
  const html = renderToStaticMarkup(createElement(policy.PolicyCustomerPreview!, { items: [item, { ...item, code: "ADP-LIQ", type: "covered", customerWording: "Liquid spills" }] }));
  assert.match(html, /Liquid spills/); assert.match(html, /Consumables, batteries/);
  assert.doesNotMatch(html, /Secret service instruction|LEGACY-1|ADP-LIQ/);
});

test("claim cause chips use tenant wording, omit inactive codes and mark the selected cause", () => {
  const causes: ClaimCause[] = [{ code: "LIQUID_SPILL", customerWording: "Liquid spill", active: true }, { code: "OLD", customerWording: "Old wording", active: false }];
  const html = renderToStaticMarkup(createElement(claim.CauseChips!, { causes, selected: "LIQUID_SPILL", onChange: () => {} }));
  assert.match(html, /aria-pressed="true"/); assert.match(html, /Liquid spill/); assert.doesNotMatch(html, /Old wording/);
});

test("claim guidance names both sides, reports remaining usage and labels advice as guidance", () => {
  const guidance: ClaimGuidance = { causeCode: "LIQUID_SPILL", covered: true, grants: [{ cover: damage, usage: [{ meterType: "claims", period: "term", now: 2, afterThisClaim: 1 }], items: data.items[0]!.grants }], excludes: data.items[0]!.excludes, sayToCustomer: "Liquid spills", notice: "Guidance only · adjudication confirms the cover" };
  const html = renderToStaticMarkup(createElement(claim.ClaimGuidanceCard!, { guidance }));
  for (const text of ["Accidental damage 24 months", "Base warranty", "excludes it", "2 claims remaining now", "1 after this claim", "Say to the customer", "Guidance only · adjudication confirms the cover"]) assert.ok(html.includes(text), text);
  assert.doesNotMatch(html, /Secret service instruction|hidden-damage|hidden-base|waiting period ended/);
});

test("every unit tab is shareable, keeps other URL state and works back to Entitlements", () => {
  assert.equal(unitTab("history"), "history"); assert.equal(unitTab("whats-covered"), "whats-covered"); assert.equal(unitTab("invalid"), "entitlements");
  for (const tab of ["history", "entitlements", "whats-covered", "cover", "identifiers", "custody", "asbuilt"]) {
    const router = createMemoryRouter([{ path: "*", element: createElement(tabs.UnitTabBar!, { search: new URLSearchParams({ tab, resolved: "old serial" }), counts: { history: 41, entitlements: "4 live · 1 closed", "whats-covered": "11 covered · 7 not", cover: "4 changes", identifiers: 3, custody: 5, asbuilt: "2 of 2" } }) }], { initialEntries: [`/units/hidden-unit?tab=${tab}`] });
    try {
      const html = renderToStaticMarkup(createElement(RouterProvider, { router }));
      assert.equal((html.match(/aria-current="page"/g) ?? []).length, 1);
      for (const text of ["History", "Entitlements", "What&#x27;s covered", "Cover since sale", "Identifiers", "Custody and ownership", "As-built", "tab=entitlements", "resolved=old+serial"]) assert.ok(html.includes(text), text);
      assert.match(html, /11 covered · 7 not/);
    } finally { router.dispose(); }
  }
});

test("new APIs page reads and send no browser actor", async () => {
  const original = globalThis.fetch; const calls: { path: string; body: unknown }[] = [];
  globalThis.fetch = async (input, init) => { calls.push({ path: String(input), body: init?.body ? JSON.parse(String(init.body)) : null }); return new Response(JSON.stringify({ items: [], total: 0, skip: 0, take: 25 }), { status: 200, headers: { "content-type": "application/json" } }); };
  try {
    await coverItemsApi.unit("unit", "covered", "liquid", 7, 7); await coverItemsApi.guidance("unit", "LIQUID_SPILL"); await coverItemsApi.items("version", "excluded", 25); await coverItemsApi.saveCauses([{ code: "CUSTOM", customerWording: "Another cause", active: true }]);
    assert.match(calls[0]!.path, /skip=7&take=7/); assert.match(calls[1]!.path, /cause=LIQUID_SPILL/); assert.match(calls[2]!.path, /skip=25/);
    assert.doesNotMatch(JSON.stringify(calls), /actorRef/); assert.equal(customerEmailEnabled, false);
  } finally { globalThis.fetch = original; }
});

test("claim intake preserves reported facts, allows a family override and does not gate submission on guidance", () => {
  const src = readFileSync(new URL("../src/claims/ClaimIntake.tsx", import.meta.url), "utf8");
  for (const text of ["reportedCauseCode: cause", "reportedDescription", "occurredDate", "Submit under", "ClaimCausePanel"]) assert.ok(src.includes(text));
  const validation = src.slice(src.indexOf("const canSubmit"), src.indexOf("const submit"));
  assert.doesNotMatch(validation, /guidance|cause|occurredDate/);
  assert.doesNotMatch(src, /ClaimLineDecider|mailto:/);
});

test("tenant cause editor offers naming, deactivation and removal subject to reference checks", () => {
  const html = renderInRouter(createElement(causeEditor.ClaimCausesEditor!, { causes: [{ code: "LIQUID_SPILL", customerWording: "Liquid spill", active: true }], onSaved: () => {} }));
  for (const text of ["Tenant claim causes", "Customer wording", "Active", "Add cause", "Save causes", "A referenced code cannot be removed"]) assert.ok(html.includes(text), text);
});

test("item drawer links preserve the selected cause and policy version without choosing among causes", () => {
  const single = new URL(startClaimUrl("unit-key", ["LIQUID_SPILL"]), "http://localhost");
  assert.equal(single.searchParams.get("cause"), "LIQUID_SPILL");
  assert.equal(new URL(startClaimUrl("unit-key", ["A", "B"]), "http://localhost").searchParams.has("cause"), false);
  const policy = new URL(policyCoverUrl({ ...damage, policyCode: "ADP-24" })!, "http://localhost");
  assert.equal(policy.searchParams.get("policy"), "ADP-24"); assert.equal(policy.searchParams.get("version"), "2");
  const html = renderInRouter(createElement(covered.CoverItemDetail!, { item: data.items[0], internal: false, unitId: "unit-key" }));
  assert.match(html, /Start a claim for this/); assert.match(html, /Base warranty excludes it/); assert.match(html, /As of/);
  assert.doesNotMatch(html, /Secret service instruction/);
});
