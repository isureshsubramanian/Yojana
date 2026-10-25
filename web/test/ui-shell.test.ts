import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import { groupHasWaiting, navigation, visibleNavigation } from "../src/shell/navigation.ts";
import { numberingChanges } from "../src/settings/numberingModel.ts";
import { filterSettings, loadCurrentValues, loadSettingsValues } from "../src/settings/registryModel.ts";
import type { NumberingSeriesSetting } from "../src/settings/numberingApi.ts";

const source = (path: string) => readFileSync(new URL(`../src/${path}`, import.meta.url), "utf8");
const sideNav = source("shell/SideNav.tsx");
const settingsLayout = source("settings/SettingsLayout.tsx");
const settingsLanding = source("settings/Settings.tsx");
const numbering = source("settings/Numbering.tsx");
const styles = source("styles/layout.css");
const routes = source("routes.tsx");
const registry = source("settings/registry.ts");

test("navigation groups hide unavailable groups and link one-screen groups directly", () => {
  const ordinary = visibleNavigation([]);
  assert.equal(ordinary.some(group => group.key === "integration"), false);
  assert.equal(ordinary.some(group => group.key === "settings"), false);
  assert.equal(ordinary.find(group => group.key === "overview")?.items.length, 1);
  const administrator = visibleNavigation(["tenant_administrator"]);
  assert.equal(administrator.find(group => group.key === "settings")?.items.length, 1);
  assert.ok(administrator.find(group => group.key === "integration"));
  assert.match(sideNav, /if \(group\.items\.length === 1\)/);
  assert.match(sideNav, /<NavLink className="item"/);
});

test("flyouts use click and native button keys with labelled navigation and managed focus", () => {
  for (const token of ["aria-expanded", "aria-controls", "aria-haspopup", "aria-label={`${current.label} screens`", "ArrowDown", "ArrowUp", "Home", "End", "Escape", "closeToButton", "mousedown"])
    assert.ok(sideNav.includes(token), token);
  assert.match(sideNav, /onClick=\{\(\) => setOpen/);
  assert.match(sideNav, /onClick=\{\(\) => setOpen\(null\)\}/);
  assert.doesNotMatch(sideNav, /onMouseEnter|onPointerEnter|ArrowRight/);
});

test("group waiting state is a dot while numbers stay inside the flyout", () => {
  const knowledge = navigation.find(group => group.key === "knowledge")!;
  assert.equal(groupHasWaiting(knowledge, {}), false);
  assert.equal(groupHasWaiting(knowledge, { questions: 0 }), false);
  assert.equal(groupHasWaiting(knowledge, { questions: 3 }), true);
  assert.match(sideNav, /nav-waiting-dot/);
  assert.match(sideNav, /<span className="count">\{countText/);
  assert.match(sideNav, /count > 99 \? "99\+"/);
  assert.doesNotMatch(sideNav, /nav-waiting-dot">\{count/);
});

test("every settings page is registered once and registry entries generate the routes", () => {
  const expected = [
    "/settings/company", "/settings/users", "/settings/sign-in-policy", "/settings/security-events", "/settings/time-zone", "/settings/currencies", "/settings/scanning", "/settings/item-serialisation",
    "/settings/numbering", "/settings/certificates", "/settings/labels", "/settings/disclosure", "/settings/jurisdictions",
    "/settings/claim-risk", "/settings/recovery", "/settings/refurbishment", "/settings/workbench-choices",
    "/settings/import-files", "/settings/knowledge-screen-changes", "/settings/privacy",
    "/settings/developers/clients", "/settings/developers/webhooks", "/settings/developers/docs",
  ];
  const paths = [...registry.matchAll(/path: "(\/settings\/[^"]+)"/g)].map(match => match[1]);
  assert.deepEqual(paths, expected);
  assert.equal(new Set(paths).size, paths.length);
  assert.match(routes, /settingsRegistry\.map/);
  assert.match(routes, /path: entry\.path\.slice\("\/settings\/"\.length\)/);
  assert.match(routes, /lazy: \(\) => import\("\.\/settings\/SettingsLayout"\)/);
});

test("landing filtering matches names and descriptions and only existing reads show values", async () => {
  const examples = [{ name: "Company", description: "Sender and reply-to" }, { name: "Refurbishment policy", description: "Identity outcomes and cycle limits" }];
  assert.deepEqual(filterSettings(examples, "reply-to").map(entry => entry.name), ["Company"]);
  assert.deepEqual(filterSettings(examples, "cycle limits").map(entry => entry.name), ["Refurbishment policy"]);
  const noNewRead = ["Users", "Scanning", "Labels", "Jurisdiction rules", "Claims risk rules", "Workbench choices", "Knowledge screen changes"];
  for (const name of noNewRead) assert.doesNotMatch(registry, new RegExp(`name: "${name}"[^\\n]*currentValue`), name);
  for (const name of ["Sign-in policy", "Time zone", "Currencies", "Document numbering", "Certificates", "Disclosure tiers", "Supplier recovery", "Refurbishment policy", "Import files"])
    assert.match(registry, new RegExp(`name: "${name}"[^\\n]*[\\s\\S]{0,250}currentValue:`), name);
  assert.match(registry, /name: "Company"[^\n]*[\s\S]{0,250}summaryKey: "company"/);
  const controller = new AbortController();
  const values = await loadCurrentValues([
    { path: "/settings/company", currentValue: async () => "Loaded" },
    { path: "/settings/users", currentValue: async () => { throw new Error("offline"); } },
  ], controller.signal);
  assert.equal(values["/settings/company"], "Loaded");
  assert.equal(values["/settings/users"], "—");
  assert.match(settingsLanding, /entry\.currentValue \|\| entry\.summaryKey/);
});

test("settings summary is read once, wording stays in the registry and missing lines use an em dash", async () => {
  const entries = [
    { path: "/settings/company", summaryKey: "company", summaryValue: (line: { counts: Record<string, number>; attention: string | null }) => ({ text: `${line.counts.entities} legal entities · ${line.counts.defaultConfigured ? "default ready" : "no default"}`, ...(line.attention ? { attention: "Choose a default" } : {}) }) },
    { path: "/settings/users", summaryKey: "users", summaryValue: (line: { counts: Record<string, number> }) => ({ text: `${line.counts.staff} staff · ${line.counts.roles} roles · ${line.counts.invitationsOpen} invitations open` }) },
    { path: "/settings/jurisdictions", summaryKey: "jurisdictions", summaryValue: (line: { counts: Record<string, number>; attention: string | null }) => ({ text: `${line.counts.ruleSets} rule sets`, ...(line.attention ? { attention: "Awaits legal confirmation" } : {}) }) },
    { path: "/settings/workbench-choices", summaryKey: "workbench_choices", summaryValue: (line: { counts: Record<string, number> }) => ({ text: `${line.counts.choices} choices · ${line.counts.twoApprovers} need two approvers` }) },
    { path: "/settings/claim-risk", summaryKey: "claims_risk", summaryValue: (line: { counts: Record<string, number> }) => ({ text: `${line.counts.claimsHeld} claim held for second review` }) },
    { path: "/settings/knowledge-screen-changes", summaryKey: "knowledge_screen_changes", summaryValue: (line: { counts: Record<string, number> }) => ({ text: `${line.counts.screensMarked} screens marked · ${line.counts.entriesToReview} entries to review` }) },
    { path: "/settings/scanning", summaryKey: "scanning", summaryValue: (line: { counts: Record<string, number> }) => ({ text: `${line.counts.endPauseMs} ms end pause · ${line.counts.repeatWindowMs} ms repeat window` }) },
    { path: "/settings/labels", summaryKey: "labels", summaryValue: () => ({ text: "unexpected" }) },
  ];
  let reads = 0;
  const values = await loadSettingsValues(entries, async () => {
    reads += 1;
    return { lines: [
      { key: "company", counts: { entities: 2, defaultConfigured: 0 }, attention: "legal_entity_not_configured" },
      { key: "users", counts: { staff: 14, roles: 6, invitationsOpen: 2 }, attention: null },
      { key: "jurisdictions", counts: { ruleSets: 4 }, attention: "awaiting_legal_confirmation" },
      { key: "workbench_choices", counts: { choices: 18, twoApprovers: 4 }, attention: null },
      { key: "claims_risk", counts: { claimsHeld: 1 }, attention: null },
      { key: "knowledge_screen_changes", counts: { screensMarked: 3, entriesToReview: 2 }, attention: null },
      { key: "scanning", counts: { endPauseMs: 300, repeatWindowMs: 1500 }, attention: null },
    ] };
  }, new AbortController().signal);
  assert.equal(reads, 1);
  assert.equal(values["/settings/company"]?.text, "2 legal entities · no default");
  assert.equal(values["/settings/company"]?.attention, "Choose a default");
  assert.equal(values["/settings/users"]?.text, "14 staff · 6 roles · 2 invitations open");
  assert.equal(values["/settings/jurisdictions"]?.attention, "Awaits legal confirmation");
  assert.equal(values["/settings/workbench-choices"]?.text, "18 choices · 4 need two approvers");
  assert.equal(values["/settings/claim-risk"]?.text, "1 claim held for second review");
  assert.equal(values["/settings/knowledge-screen-changes"]?.text, "3 screens marked · 2 entries to review");
  assert.equal(values["/settings/scanning"]?.text, "300 ms end pause · 1500 ms repeat window");
  assert.equal(values["/settings/labels"]?.text, "—");
  for (const phrase of ["invitationsOpen\"), \"invitation\"", "awaiting_legal_confirmation", "held for second review", "need\"} two approvers", "entriesToReview\"), \"entry\", \"entries\")} to review", "repeatWindowMs\").toLocaleString()"])
    assert.ok(registry.includes(phrase), phrase);
  assert.match(settingsLanding, /settingsSummaryApi\.get/);
  assert.match(settingsLanding, /StatusBadge tone="warn"/);
});

test("settings side navigation filters, highlights the current page and becomes a selector below 960 px", () => {
  assert.match(settingsLayout, /filterSettings\(entries, query\)/);
  assert.match(settingsLayout, /aria-current=\{currentPath === entry\.path \? "page"/);
  assert.match(settingsLayout, /All settings/);
  assert.match(settingsLayout, /settings-mobile-select/);
  assert.match(styles, /@media \(max-width: 960px\)[\s\S]*\.settings-nav \{ display: none; \}[\s\S]*\.settings-mobile-select \{ display: grid/);
});

test("numbering batches changed rows behind Save and guards unsaved navigation", () => {
  const saved: NumberingSeriesSetting[] = [
    { series: "claims", label: "Claims", mode: "sequence", example: "CLM-2026-00001", gapBehaviour: "gaps" },
    { series: "returns", label: "Returns", mode: "gap_free", example: "RMA-2026-00001", gapBehaviour: "consecutive" },
  ];
  assert.deepEqual(numberingChanges(saved, [{ ...saved[0]!, mode: "gap_free" }, saved[1]!]), [{ series: "claims", mode: "gap_free" }]);
  for (const token of ["Promise.allSettled(changes.map", "Save", "Cancel", "useBlocker", "beforeunload", "blocker.proceed()", "blocker.reset()"])
    assert.ok(numbering.includes(token), token);
  assert.equal((numbering.match(/Sequence is the operational default/g) ?? []).length, 1);
});

test("knowledge screen changes is a standalone registered settings page", () => {
  assert.match(registry, /group: "Data and knowledge", name: "Knowledge screen changes"/);
  assert.match(registry, /path: "\/settings\/knowledge-screen-changes"/);
  const page = source("settings/KnowledgeScreenChangesPage.tsx");
  assert.match(page, /<KnowledgeScreenChanges \/>/);
  assert.match(page, /tenant_administrator/);
});
