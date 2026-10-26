import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { createRequire } from "node:module";
import test from "node:test";
import { fileURLToPath } from "node:url";
import { build } from "esbuild";
import { createElement, type ComponentType } from "react";
import { renderToStaticMarkup } from "react-dom/server";

const bundle = await build({ entryPoints: [fileURLToPath(new URL("../src/components/CollapsibleCard.tsx", import.meta.url))],
  bundle: true, platform: "node", format: "cjs", jsx: "automatic", packages: "external", write: false });
const compiled = { exports: {} as { CollapsibleCard: ComponentType<{ title: string; storageKey: string; children: import("react").ReactNode }> } };
new Function("require", "module", "exports", bundle.outputFiles[0]!.text)(createRequire(import.meta.url), compiled, compiled.exports);
const source = (path: string) => readFileSync(new URL(`../src/${path}`, import.meta.url), "utf8");

test("collapsible card is keyboard-operable and reports its expanded state", () => {
  Reflect.deleteProperty(globalThis, "window");
  const html = renderToStaticMarkup(createElement(compiled.exports.CollapsibleCard,
    { title: "Partner feed messages", storageKey: "history:test", children: createElement("p", null, "Rows") }));
  assert.match(html, /<button type="button"[^>]+aria-label="Collapse Partner feed messages"/);
  assert.match(html, /aria-expanded="true"/);
  assert.match(html, /aria-controls="collapsible-/);
  assert.match(html, />Rows</);
});

test("a card remembered as collapsed does not mount or fetch its children", () => {
  let mounted = 0;
  function FetchingChild() { mounted += 1; return createElement("p", null, "fetched"); }
  Object.defineProperty(globalThis, "window", { configurable: true, value: { localStorage: {
    getItem: () => "collapsed", setItem: () => undefined,
  } } });
  try {
    const html = renderToStaticMarkup(createElement(compiled.exports.CollapsibleCard,
      { title: "Uploaded files", storageKey: "history:test", children: createElement(FetchingChild) }));
    assert.equal(mounted, 0);
    assert.match(html, /aria-label="Expand Uploaded files"/);
    assert.match(html, /aria-expanded="false"/);
    assert.doesNotMatch(html, /fetched/);
  } finally { Reflect.deleteProperty(globalThis, "window"); }
});

test("upload history renders the three accepted cards and keeps their fetches separate", () => {
  const screen = source("p7/ImportHistory.tsx");
  for (const title of ["Uploaded files", "Partner feed messages", "File comparisons"])
    assert.match(screen, new RegExp(`title=\\"${title}\\"`));
  assert.match(screen, /Message bodies are not kept; the payload hash proves what was received\./);
  assert.match(screen, />Back to imports</);
  assert.match(screen, /useGridQuery\("\/api\/imports\/history"/);
  assert.match(screen, /useGridQuery\("\/api\/imports\/history\/feed-messages"/);
  assert.match(screen, /useGridQuery\("\/api\/imports\/history\/comparisons"/);
  assert.match(screen, /storageKey=\{`upload-history:\$\{viewer\}:/);
});

test("imports opens an opaque job query and gives an unavailable job a note", () => {
  const imports = source("p7/screens.tsx");
  assert.match(imports, /opaqueParam\(params, "job"\)/);
  assert.match(imports, /await selectJob\(requestedJob\)/);
  assert.match(imports, /The requested import job is not available\./);
  assert.match(imports, /role="status"/);
});
