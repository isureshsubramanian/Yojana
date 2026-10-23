import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import { securityEventsApi, securityEventQuery } from "../src/settings/securityEventsApi.ts";
import { browserName, securityEventLabel, securityEventTone, shiftDay, summaryKpis } from "../src/settings/securityEventsModel.ts";

const source = (path: string) => readFileSync(new URL(`../src/${path}`, import.meta.url), "utf8");
const page = source("settings/SecurityEvents.tsx");
const model = source("settings/securityEventsModel.ts");
const routes = source("routes.tsx");
const registry = source("settings/registry.ts");
const layout = source("settings/SettingsLayout.tsx");

test("security events keeps the approved KPI, filters, paged event log and drawer", () => {
  for (const label of ["Sign-ins today", "Failed sign-ins today", "Lockouts today", "Password resets, 7 days", "Sign-in method changes, 7 days"])
    assert.match(model, new RegExp(label.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")));
  for (const token of ["<DataGrid", "nextCursor", "PAGE_SIZE = 25", "Any event", "User name or e-mail", "Last 7 days", "Custom…", "openDrawer", "row.userName", "row.actorName"])
    assert.ok(page.includes(token), token);
  assert.doesNotMatch(page, /Export|CSV|Excel/);
  assert.doesNotMatch(page, /Aggregate not supplied by the event log/);
  assert.match(page, /securityEventsApi\.summary\(/);
  assert.match(page, /summaryKpis\(summary\)/);
});

test("security event labels, tones, device summaries and date ranges are stable", () => {
  assert.equal(securityEventLabel("password_reset_requested"), "Reset link sent");
  assert.equal(securityEventLabel("future_event"), "Future event");
  assert.equal(securityEventTone("locked_out"), "bad");
  assert.equal(securityEventTone("passkey_added"), "ok");
  assert.equal(browserName("Mozilla/5.0 Chrome/141.0 Safari/537.36"), "Chrome");
  assert.equal(browserName("Mozilla/5.0 Chrome/141.0 Safari/537.36 Edg/141.0"), "Edge");
  assert.equal(shiftDay("2026-09-01", -6), "2026-08-26");
});

test("security event client sends only endpoint-supported filters and keeps resolved names", async () => {
  const calls: string[] = [];
  const original = globalThis.fetch;
  globalThis.fetch = (async (input: string | URL | Request) => {
    calls.push(String(input));
    return new Response(JSON.stringify({ items: [{ occurredAt: "2026-09-01T08:00:00Z", kind: "sign_in_succeeded", userName: "Meera Pillai", actorName: null, clientAddress: "198.51.100.20", userAgent: "Firefox/142", reason: "passkey" }], nextCursor: "next" }), { status: 200 });
  }) as typeof fetch;
  try {
    const result = await securityEventsApi.page({ kind: "sign_in_succeeded", userId: "user-1", from: "2026-09-01T00:00:00Z", to: "2026-09-02T00:00:00Z", cursor: "cursor value", take: 25 });
    assert.equal(result.items[0]?.userName, "Meera Pillai");
    assert.equal(result.nextCursor, "next");
    assert.equal(calls[0], "/api/security/events?kind=sign_in_succeeded&userId=user-1&from=2026-09-01T00%3A00%3A00Z&to=2026-09-02T00%3A00%3A00Z&cursor=cursor+value&take=25");
    assert.equal(securityEventQuery({ take: 25 }), "take=25");
  } finally { globalThis.fetch = original; }
});

test("security events is a tenant-administrator setting and unknown settings stay in SettingsLayout", () => {
  assert.match(registry, /name: "Security events"[\s\S]{0,220}path: "\/settings\/security-events"[\s\S]{0,160}roles: administrator/);
  assert.match(routes, /\.\.\.settingsChildren,[\s\S]{0,100}\{ path: "\*", lazy: \(\) => import\("\.\/settings\/SettingsNotFound"\) \}/);
  assert.match(layout, /<SettingsNavigation currentPath=\{location\.pathname\} \/>/);
  const notFound = source("settings/SettingsNotFound.tsx");
  assert.match(notFound, /Setting not found/);
  assert.match(notFound, /to="\/settings">All settings/);
});

test("approved sign-in tabs remain and duplicate back buttons are removed", () => {
  for (const file of ["settings/Users.tsx", "settings/SignInPolicy.tsx", "settings/SecurityEvents.tsx"]) {
    const text = source(file);
    assert.match(text, /aria-label="Sign-in settings"/);
    for (const path of ["/settings/users", "/settings/sign-in-policy", "/settings/security-events"])
      assert.ok(text.includes(`to="${path}"`), `${file}: ${path}`);
  }
  assert.doesNotMatch(source("settings/Company.tsx"), /Back to settings/);
  assert.doesNotMatch(source("settings/SignInPolicy.tsx"), /Back to settings/);
});

test("security event cards show the summary counts, an ellipsis while loading and a plain failure note", async () => {
  const cards = summaryKpis({ signIns: 1204, failedSignIns: 3, lockouts: 0, passwordResets: 2, methodChanges: 5 });
  assert.deepEqual(cards.map(card => card.label), ["Sign-ins today", "Failed sign-ins today", "Lockouts today", "Password resets, 7 days", "Sign-in method changes, 7 days"]);
  assert.deepEqual(cards.map(card => card.value), [(1204).toLocaleString(), "3", "0", "2", "5"]);
  assert.ok(summaryKpis(null).every(card => card.value === "…" && !card.detail));
  assert.ok(summaryKpis("failed").every(card => card.value === "—" && card.detail === "Couldn't load"));

  const calls: string[] = [];
  const original = globalThis.fetch;
  globalThis.fetch = (async (input: string | URL | Request) => {
    calls.push(String(input));
    return new Response(JSON.stringify({ signIns: 1, failedSignIns: 0, lockouts: 0, passwordResets: 0, methodChanges: 0 }), { status: 200 });
  }) as typeof fetch;
  try {
    assert.equal((await securityEventsApi.summary("2026-09-01T00:00:00.000Z", "2026-08-26T00:00:00.000Z")).signIns, 1);
    assert.equal(calls[0], "/api/security/events/summary?todayFrom=2026-09-01T00%3A00%3A00.000Z&weekFrom=2026-08-26T00%3A00%3A00.000Z");
  } finally { globalThis.fetch = original; }
});
