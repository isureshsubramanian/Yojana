import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import { groupHasWaiting, navigation, visibleNavigation } from "../src/shell/navigation.ts";
import { numberingChanges } from "../src/settings/numberingModel.ts";
import { filterSettings, loadCurrentValues } from "../src/settings/registryModel.ts";
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
    "/settings/company", "/settings/users", "/settings/sign-in-policy", "/settings/security-events", "/settings/time-zone", "/settings/currencies",
    "/settings/numbering", "/settings/certificates", "/settings/labels", "/settings/disclosure", "/settings/jurisdictions",
    "/settings/claim-risk", "/settings/recovery", "/settings/refurbishment", "/settings/workbench-choices",
    "/settings/import-files", "/settings/knowledge-screen-changes",
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
  const noNewRead = ["Users", "Labels", "Jurisdiction rules", "Workbench choices", "Knowledge screen changes"];
  for (const name of noNewRead) assert.doesNotMatch(registry, new RegExp(`name: "${name}"[^\\n]*currentValue`), name);
  for (const name of ["Company", "Sign-in policy", "Time zone", "Currencies", "Document numbering", "Certificates", "Disclosure tiers", "Claims risk rules", "Supplier recovery", "Refurbishment policy", "Import files"])
    assert.match(registry, new RegExp(`name: "${name}"[^\\n]*[\\s\\S]{0,250}currentValue:`), name);
  const controller = new AbortController();
  const values = await loadCurrentValues([
    { path: "/settings/company", currentValue: async () => "Loaded" },
    { path: "/settings/users", currentValue: async () => { throw new Error("offline"); } },
  ], controller.signal);
  assert.equal(values["/settings/company"], "Loaded");
  assert.equal(values["/settings/users"], "—");
  assert.match(settingsLanding, /entry\.currentValue &&/);
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
