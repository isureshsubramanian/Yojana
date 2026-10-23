import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";

const read = (path: string) => readFileSync(new URL(`../src/${path}`, import.meta.url), "utf8");
const routes = read("routes.tsx");
const screen = read("integration/ConnectionVerification.tsx");
const tabs = read("integration/ConnectionTabs.tsx");
const api = read("integration/verificationApi.ts");
const connections = read("integration/Connections.tsx");

test("verification record is routed from a connection and uses the approved tabs", () => {
  assert.match(routes, /integration\/connections\/:connectionId\/verification/);
  assert.match(connections, />Verification record</);
  for (const tab of ["Health", "Sign-in", "Verification record", "Settings"]) assert.match(tabs, new RegExp(`>${tab}<`));
  assert.doesNotMatch(tabs, />Extension</);
});

test("verification record shows manifest figures, client-paged facts and the fact drawer", () => {
  for (const label of ["Checked against a live system", "Documented facts", "Assumed facts", "Edge scenarios on simulator"]) assert.match(screen, new RegExp(`"${label}"`));
  assert.match(screen, /const factPageSize = 25/);
  assert.match(screen, /<DataGrid/);
  assert.match(screen, /onRowClick=\{openFact\}/);
  assert.match(screen, /openDrawer\(\{ eyebrow: `\$\{human\(fact\.kind\)\} fact`/);
});

test("acceptance runs support the whole operator workflow without a browser actor", () => {
  for (const action of ["Start acceptance run", "Import result file", "Save checklist item", "Complete acceptance run"]) assert.match(screen, new RegExp(action));
  for (const path of ["/runs`", "/items/${itemId}", "/imports", "/complete"]) assert.ok(api.includes(path), path);
  assert.doesNotMatch(api, /actor|startedBy|completedBy/);
  assert.match(screen, /not_applicable[^\n]*note\.trim/);
  assert.match(screen, /No credentials, payloads or secrets are stored here/);
});

test("run and item icon-style rows are accessible", () => {
  assert.match(screen, /className="verification-run"[^\n]*aria-label=\{`Open acceptance run/);
  assert.match(screen, /className="verification-run"[^\n]*title="Open acceptance run"/);
  assert.match(screen, /aria-label=\{`Mark \$\{item\.label\}`\}/);
  assert.match(screen, /title="Mark checklist item"/);
});
