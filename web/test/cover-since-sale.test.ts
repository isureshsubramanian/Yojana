import assert from "node:assert/strict";
import test from "node:test";
import { readFileSync } from "node:fs";
import { createRequire } from "node:module";
import { fileURLToPath } from "node:url";
import { build } from "esbuild";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { createMemoryRouter, RouterProvider } from "react-router";
import { changedFamilies, changeLabel, coverChangeLabel, coverQuery, moneyLabel } from "../src/entitlements/coverModel.ts";
import type { CoverFamily, CoverSinceSale } from "../src/entitlements/coverTypes.ts";

const bundle = await build({ entryPoints: [fileURLToPath(new URL("../src/entitlements/CoverSinceSalePanel.tsx", import.meta.url))], bundle: true,
  platform: "node", format: "cjs", jsx: "automatic", packages: "external", loader: { ".css": "empty" }, write: false });
const compiled = { exports: {} as { CoverSinceSaleView: import("react").ComponentType<{ data: CoverSinceSale }> } };
new Function("require", "module", "exports", bundle.outputFiles[0]!.text)(createRequire(import.meta.url), compiled, compiled.exports);

test("populated unit comparison renders the sale, named policies, separate currencies and amendment evidence", () => {
  const data: CoverSinceSale = { unitId: "hidden-unit-key", serial: "RS11-2509-04417", itemName: "RS-1100 rack server", customerName: "Meridian Data Labs",
    saleDate: "2025-11-04", saleDocumentNumber: "INV-2025-10882", changeCount: 1, value: [{ currency: "USD", amount: 180 }, { currency: "EUR", amount: 640 }],
    coveredTo: "2026-11-24", notice: "Covers not taken are omitted", changes: [], families: [{ familyName: "Base warranty", change: "amended", closedReason: null,
      atSale: { policyName: "Standard 12 months", startDate: "2025-11-04", endDate: "2026-11-03", status: "active", future: false },
      today: { policyName: "Standard 12 months", startDate: "2025-11-04", endDate: "2026-11-24", status: "active", future: false }, changes: [{ eventId: "hidden-event-key",
        kind: "amended", familyName: "Base warranty", policyName: "Standard 12 months", businessDate: "2026-08-10", monthsAfterSale: 9, documentNumber: null, documentLine: null,
        amount: null, currency: null, payerName: "Meridian Data Labs", actorName: "Fictional Service Agent", approverName: "Fictional Service Manager", reason: "goodwill", counted: true,
        fields: [{ field: "end_date", before: "2026-11-03", after: "2026-11-24" }] }] }] };
  const html = renderToStaticMarkup(createElement(compiled.exports.CoverSinceSaleView, { data }));
  for (const text of ["INV-2025-10882", "Standard 12 months", "Base warranty", "USD 180", "EUR 640", "2026-11-03", "2026-11-24", "Fictional Service Manager", "Covers not taken are omitted"]) assert.ok(html.includes(text), text);
  assert.doesNotMatch(html, /hidden-unit-key|hidden-event-key|USD 820/);
  const empty = renderToStaticMarkup(createElement(compiled.exports.CoverSinceSaleView, { data: { ...data, saleDate: null } }));
  assert.match(empty, /No sale recorded/);
  assert.doesNotMatch(empty, /At sale and today/);
});

test("money preserves separate currencies and missing refund amounts", () => {
  assert.equal(moneyLabel([{ currency: "USD", amount: 180 }, { currency: "EUR", amount: 640 }]), "USD 180 · EUR 640");
  assert.equal(moneyLabel([]), "No amount recorded");
  assert.match(moneyLabel([{ currency: "USD", amount: -20 }]), /-20/);
});
test("changed-only comparisons exclude natural expiry, transfers and not-taken cover", () => {
  const rows = ["added", "upgraded", "amended", "ended", "transferred", "not_taken", "same"].map((change) => ({ change } as CoverFamily));
  assert.equal(changedFamilies(rows, false).length, 7);
  assert.deepEqual(changedFamilies(rows, true).map((r) => r.change), ["added", "upgraded", "amended"]);
  assert.equal(changeLabel("closed_early"), "Closed early");
});
test("report chips name the actual cover rather than only repeating a change kind", () => {
  assert.equal(coverChangeLabel({ kind: "added", familyName: "Extended term", policyName: "Extended 24 months" }), "+ Extended 24 months");
  assert.equal(coverChangeLabel({ kind: "upgraded", familyName: "Service level", policyName: "Next business day" }), "Service level → Next business day");
  assert.equal(coverChangeLabel({ kind: "amended", familyName: "Base warranty", policyName: "Standard 12 months" }), "Base warranty · Amended");
});
test("report renders its named filters, five KPIs, by-kind table, grid and disabled loading exports", async () => {
  const output = await build({ entryPoints: [fileURLToPath(new URL("../src/entitlements/CoverChanges.tsx", import.meta.url))], bundle: true,
    platform: "node", format: "cjs", jsx: "automatic", packages: "external", loader: { ".css": "empty" }, write: false });
  const module = { exports: {} as { Component: import("react").ComponentType } };
  new Function("require", "module", "exports", output.outputFiles[0]!.text)(createRequire(import.meta.url), module, module.exports);
  const session = { user: { id: "hidden-user-key", displayName: "Fictional Service Agent", email: "fictional@example.invalid" }, tenant: { code: "test", name: "Fictional Preview" }, roles: [], tenants: [] };
  const router = createMemoryRouter([{ id: "root", path: "*", loader: () => session, element: createElement(module.exports.Component) }],
    { initialEntries: ["/entitlements/cover-changes"], hydrationData: { loaderData: { root: session } } });
  try {
    const html = renderToStaticMarkup(createElement(RouterProvider, { router }));
    for (const text of ["Cover changes since sale", "Sold from", "Sold to", "Customer", "Item family", "Units sold in period", "Cover changed since sale", "Bought after the sale", "Typical time to buy", "Closed early", "Median months after sale", "Changes since sale", "CSV", "Excel"]) assert.ok(html.includes(text), text);
    assert.ok(html.indexOf("<h2>What changed") < html.indexOf("<h2>Units"));
    assert.match(html, /disabled/);
    assert.doesNotMatch(html, /hidden-user-key/);
  } finally { router.dispose(); }
});
test("filter and cursor contract keeps the opaque cursor and names out of identity parameters", () => {
  const q = new URLSearchParams(coverQuery({ serial: "RS 1100", itemFamily: "rack_servers", soldFrom: "2025-11-04" }, "a+b="));
  assert.equal(q.get("serial"), "RS 1100");
  assert.equal(q.get("cursor"), "a+b=");
  assert.equal(q.has("actorRef"), false);
});
test("unit screen has comparison, change selection, full named record and no identifier fallbacks", () => {
  const src = readFileSync(new URL("../src/entitlements/CoverSinceSalePanel.tsx", import.meta.url), "utf8");
  for (const text of ["At a glance", "At sale and today, by cover", "Changes since sale", "No sale recorded", "Before and after", "Approved by", "Payer", "Changed only"]) assert.ok(src.includes(text));
  assert.doesNotMatch(src, /shortId|actorRef|\.slice\(0,\s*\d+\)/);
  assert.match(src, /controller\.abort/);
});
test("report uses server cursors, shared grid and exports with by-kind card before unit list", () => {
  const src = readFileSync(new URL("../src/entitlements/CoverChanges.tsx", import.meta.url), "utf8");
  assert.match(src, /cursorStack/);
  assert.match(src, /data\.nextCursor/);
  assert.match(src, /DataGrid/);
  assert.match(src, /ExportButtons/);
  assert.match(src, /PartyField/);
  assert.ok(src.indexOf("<h2>What changed") < src.indexOf("<h2>Units"));
  assert.doesNotMatch(src, /shortId|actorRef/);
  assert.match(src, /100000/);
});
