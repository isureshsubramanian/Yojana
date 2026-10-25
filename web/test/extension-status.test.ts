import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import { extensionFigure, extensionVersionDetail, pollInterval } from "../src/integration/extensionModel.ts";

const read = (path: string) => readFileSync(new URL(`../src/${path}`, import.meta.url), "utf8");
const routes = read("routes.tsx");
const screen = read("integration/ConnectionExtension.tsx");
const tabs = read("integration/ConnectionTabs.tsx");
const api = read("integration/extensionApi.ts");
const connections = read("integration/Connections.tsx");

test("extension screen is routed and capability-gated without a connector-id check", () => {
  assert.match(routes, /integration\/connections\/:connectionId\/extension/);
  assert.match(tabs, /features\??\.tenantSideExtension/);
  assert.match(connections, /detail\.features\??\.tenantSideExtension/);
  assert.doesNotMatch(screen, /CX02|connectorCode\s*===/);
  assert.doesNotMatch(tabs, /CX02|connectorCode\s*===/);
});

test("extension screen follows the approved figures, grid, access card and drawer", () => {
  for (const label of ["Installed version", "Data pages reachable", "Permissions", "Change notifications"])
    assert.match(screen, new RegExp(`"${label}"`));
  assert.match(screen, /<DataGrid/);
  assert.match(screen, /onRowClick=\{openPage\}/);
  assert.match(screen, /id="extension-access"/);
  for (const label of ["Contract", "Notifications", "Fallback poll"])
    assert.ok(screen.includes(`["${label}"`));
});

test("check-now and installation guide controls use server-owned paths and accessible icon text", () => {
  assert.match(api, /\/check`[^\n]*method: "POST"/);
  assert.match(screen, /href=\{view\.installationGuidePath\}/);
  assert.match(screen, /className="icon-btn" aria-label="Check the tenant-side extension now" title="Check now"/);
  assert.doesNotMatch(api, /actor|checkedBy/);
});

test("unknown figures render as a dash rather than zero", () => {
  assert.equal(extensionFigure(null), "—");
  assert.equal(extensionFigure(0), "0");
  assert.equal(pollInterval(null), "—");
  assert.equal(pollInterval(900), "every 15 min");
  assert.equal(extensionVersionDetail("1.0.0.0", "1.0.0.0"), "this release ships the installed version");
});
