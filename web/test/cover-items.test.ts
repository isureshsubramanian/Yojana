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
  const result = await build({ entryPoints: [fileURLToPath(new URL(path, import.meta.url))], bundle: true, platform: "node", format: "cjs", jsx: "automatic", packages: "external", loader: { ".css": "empty" }, write: false });
  const module = { exports: {} as T };
  new Function("require", "module", "exports", result.outputFiles[0]!.text)(createRequire(import.meta.url), module, module.exports);
  return module.exports;
}
const { coverItemsApi, unitTab, startClaimUrl, policyCoverUrl } = await compile<typeof import("../src/entitlements/coverItems.ts")>("../src/entitlements/coverItems.ts");
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
  const guidance: ClaimGuidance = { causeCode: "LIQUID_SPILL", covered: true, grants: [{ cover: damage, usage: [{ meterType: "claims", period: "term", now: 2, afterThisClaim: null }], items: data.items[0]!.grants, waitingPeriodUntil: "2026-09-03" }], excludes: data.items[0]!.excludes, sayToCustomer: "Liquid spills", notice: "Guidance only · adjudication confirms the cover" };
  const html = renderToStaticMarkup(createElement(claim.ClaimGuidanceCard!, { guidance }));
  for (const text of ["Accidental damage 24 months", "Base warranty", "excludes it", "2 claims remaining now", "— after this claim", "Inside the waiting period until", "2026", "Say to the customer", "Guidance only · adjudication confirms the cover"]) assert.ok(html.includes(text), text);
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
    await coverItemsApi.unit("unit", "covered", "liquid", 7, 7); await coverItemsApi.guidance("unit", "LIQUID_SPILL", "2026-09-10", "2026-09-01"); await coverItemsApi.items("version", "excluded", 25); await coverItemsApi.saveCauses([{ code: "CUSTOM", customerWording: "Another cause", active: true }], "loaded-version");
    assert.match(calls[0]!.path, /skip=7&take=7/); assert.match(calls[1]!.path, /cause=LIQUID_SPILL/); assert.match(calls[1]!.path, /claimDate=2026-09-10/); assert.match(calls[1]!.path, /occurredDate=2026-09-01/); assert.match(calls[2]!.path, /skip=25/);
    assert.doesNotMatch(JSON.stringify(calls), /actorRef/); assert.equal((calls[3]!.body as { expectedVersion: string }).expectedVersion, "loaded-version");
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
  const html = renderInRouter(createElement(causeEditor.ClaimCausesEditor!, { causes: [{ code: "LIQUID_SPILL", customerWording: "Liquid spill", active: true }], version: "loaded-version", onSaved: () => {} }));
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


test("legacy wording over 500 characters shows both warnings without blocking an unchanged draft", () => {
  const long = { ...item, customerWording: "W".repeat(1201), tooLongForCustomerSheet: true };
  const html = renderInRouter(createElement(policy.PolicyItemsEditor!, { versionId: "version", items: [long], editable: true, dirty: true, onChange: () => {} }));
  assert.match(html, /Needs category/); assert.match(html, /Too long for the customer sheet/);
  const drawer = renderToStaticMarkup(createElement(policy.ItemForm!, { item: long, causes: [], editable: true, fresh: false, onSave: () => {}, onRemove: () => {} }));
  assert.doesNotMatch(drawer, /disabled=""[^>]*>Keep in draft/);
  assert.ok(drawer.includes(long.customerWording));
});

test("future-only grants render under Covered with a From date and no excluded-by label", () => {
  const future = { ...data.items[0]!, excludes: [], grants: [{ ...data.items[0]!.grants[0]!, cover: { ...damage, future: true, from: "2026-11-25" } }] };
  const html = renderToStaticMarkup(createElement(covered.CoverItemsTable!, { data: { ...data, items: [future] }, covered: true }));
  assert.match(html, /Covered by/); assert.match(html, /From/); assert.doesNotMatch(html, /Excluded by/);
});

// Run event callbacks and effects against persistent hook slots. No DOM or additional test packages are needed.
async function interactive(path: string) {
  const react = createRequire(import.meta.url)("react");
  const router = createRequire(import.meta.url)("react-router");
  const slots: any[] = []; let cursor = 0; let pending: (() => void)[] = [];
  const hooks = {
    ...react,
    useState(initial: any) { const index = cursor++; if (!(index in slots)) slots[index] = typeof initial === "function" ? initial() : initial; return [slots[index], (value: any) => { slots[index] = typeof value === "function" ? value(slots[index]) : value; }]; },
    useRef(initial: any) { const index = cursor++; return slots[index] ??= { current: initial }; },
    useEffect(effect: () => (() => void) | void, dependencies: any[]) {
      const index = cursor++; const previous = slots[index];
      if (previous && dependencies.every((d, i) => Object.is(d, previous.dependencies[i]))) return;
      pending.push(() => { previous?.cleanup?.(); slots[index] = { dependencies, cleanup: effect() }; });
    },
    useCallback(fn: any) { return fn; }, useMemo(fn: any) { return fn(); },
    useSyncExternalStore(_subscribe: any, snapshot: any) { return snapshot(); }, useId() { return "fixture-id"; },
  };
  const result = await build({ entryPoints: [fileURLToPath(new URL(path, import.meta.url))], bundle: true, platform: "node", format: "cjs", jsx: "automatic", packages: "external", loader: { ".css": "empty" }, write: false,
    plugins: [{ name: "reference-boundary", setup(builder) { builder.onResolve({ filter: /ReferenceFields$/ }, () => ({ path: "references", namespace: "fixture" })); builder.onLoad({ filter: /.*/, namespace: "fixture" }, () => ({ contents: "export const useReferenceNames = () => ({ item: () => 'Rack server', party: () => 'Meridian Labs' }); export const ItemField = () => null; export const PartyField = () => null;" })); } }],
  });
  const module = { exports: {} as Record<string, (props: any) => any> };
  const require = createRequire(import.meta.url);
  new Function("require", "module", "exports", result.outputFiles[0]!.text)((name: string) => name === "react" ? hooks : name === "react-router" ? { ...router, useNavigate: () => () => {}, useSearchParams: () => [new URLSearchParams(), () => {}] } : require(name), module, module.exports);
  return {
    render(name: string, props = {}) { cursor = 0; pending = []; return module.exports[name]!(props); },
    effects() { for (const effect of pending) effect(); },
    dispose() { for (const slot of slots) slot?.cleanup?.(); },
  };
}
function elements(tree: any): any[] {
  if (!tree || typeof tree !== "object") return [];
  if (Array.isArray(tree)) return tree.flatMap(elements);
  return [tree, ...elements(tree.props?.children)];
}

test("changing a cause preserves the desk's manual family override while guidance updates", async () => {
  const instance = await interactive("../src/claims/ClaimIntake.tsx");
  const render = () => elements(instance.render("Component"));
  const causePanel = (nodes: any[]) => nodes.find((n) => n.props?.onGuidance && n.props?.onCause);
  const family = (nodes: any[]) => nodes.find((n) => n.props?.["aria-label"] === "Line 1 coverage family");
  const guidance = { causeCode: "LIQUID_SPILL", covered: true, grants: [{ cover: damage, usage: [], items: [], waitingPeriodUntil: null }], excludes: [], sayToCustomer: "Liquid spills", notice: "Guidance only" };
  let nodes = render(); causePanel(nodes).props.onGuidance(guidance);
  nodes = render(); assert.equal(family(nodes).props.value, "accidental_damage");
  family(nodes).props.onChange({ target: { value: "base_warranty" } });
  nodes = render(); causePanel(nodes).props.onCause("DROPPED_KNOCKED");
  nodes = render(); causePanel(nodes).props.onGuidance({ ...guidance, causeCode: "DROPPED_KNOCKED" });
  nodes = render(); assert.equal(family(nodes).props.value, "base_warranty");
});

test("one view response supplies summary and both tables, with one debounced request per search change", async () => {
  const instance = await interactive("../src/entitlements/WhatsCoveredPanel.tsx");
  const original = globalThis.fetch; const calls: string[] = [];
  globalThis.fetch = async (input) => { calls.push(String(input)); return new Response(JSON.stringify({ ...data, coveredTotal: 1, excludedTotal: 0 }), { headers: { "content-type": "application/json" } }); };
  const props = { unitId: "unit-key", summary: { ...data, coveredTotal: 1, excludedTotal: 0 } };
  try {
    let nodes = elements(instance.render("WhatsCoveredPanel", props)); instance.effects();
    assert.equal(calls.length, 0);
    const search = () => nodes.find((n) => n.props?.["aria-label"] === "Find a cause, part or service");
    search().props.onChange({ target: { value: "liq" } });
    nodes = elements(instance.render("WhatsCoveredPanel", props)); instance.effects();
    search().props.onChange({ target: { value: "liquid" } });
    nodes = elements(instance.render("WhatsCoveredPanel", props)); instance.effects();
    await new Promise((resolve) => setTimeout(resolve, 220));
    assert.equal(calls.length, 1); assert.match(calls[0]!, /sectionPages=true&search=liquid/);
    assert.match(calls[0]!, /coveredTake=7/); assert.match(calls[0]!, /excludedTake=7/);
    const unit = readFileSync(new URL("../src/units/UnitDetail.tsx", import.meta.url), "utf8");
    assert.equal((unit.match(/coverItemsApi.sections\(/g) ?? []).length, 1);
    assert.match(unit, /whatsCovered\?\.customerEmailEnabled/);
  } finally { instance.dispose(); globalThis.fetch = original; }
});
