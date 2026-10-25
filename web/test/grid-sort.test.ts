import assert from "node:assert/strict";
import { createRequire } from "node:module";
import test from "node:test";
import { fileURLToPath } from "node:url";
import { build } from "esbuild";
import { createElement, type ComponentType } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { nextGridSort, pageSizeChoices, readGridPreference } from "../src/components/gridPreferences.ts";
import { resetGridQuery } from "../src/components/useGridQuery.ts";

async function compile<T>(path: string): Promise<T> {
  const result = await build({ entryPoints: [fileURLToPath(new URL(path, import.meta.url))], bundle: true, platform: "node", format: "cjs", jsx: "automatic", packages: "external", write: false });
  const module = { exports: {} as T };
  new Function("require", "module", "exports", result.outputFiles[0]!.text)(createRequire(import.meta.url), module, module.exports);
  return module.exports;
}

const { SortHeader } = await compile<{ SortHeader: ComponentType<any> }>("../src/components/SortHeader.tsx");
const { PageSizeSelect } = await compile<{ PageSizeSelect: ComponentType<any> }>("../src/components/PageSizeSelect.tsx");

test("sort header reports state and the next keyboard-button action", () => {
  const html = renderToStaticMarkup(createElement(SortHeader, { label: "Received", column: { key: "received", defaultDir: "desc" }, sort: { key: "received", dir: "desc" }, screenDefault: { key: "identifier", dir: "asc" }, onSort: () => undefined }));
  assert.match(html, /<button type="button"/);
  assert.match(html, /aria-label="Sort by Received, ascending"/);
  assert.match(html, /title="Sort by Received, ascending"/);
  assert.match(html, />↓</);
});

test("sort cycles declared default, the other direction, then the screen default", () => {
  const screen = { key: "identifier", dir: "asc" } as const;
  const first = nextGridSort(screen, { key: "received", defaultDir: "desc" }, screen);
  const second = nextGridSort(first, { key: "received", defaultDir: "desc" }, screen);
  const third = nextGridSort(second, { key: "received", defaultDir: "desc" }, screen);
  assert.deepEqual([first, second, third], [
    { key: "received", dir: "desc" }, { key: "received", dir: "asc" }, screen,
  ]);
});

test("page sizes retain a screen default and size changes discard page and cursor", () => {
  assert.deepEqual(pageSizeChoices(12), [12, 25, 50, 100, 200]);
  const html = renderToStaticMarkup(createElement(PageSizeSelect, { value: 12, defaultSize: 12, onChange: () => undefined }));
  assert.match(html, /aria-label="Rows per page"/);
  for (const size of [12, 25, 50, 100, 200]) assert.match(html, new RegExp(`value="${size}"`));
  assert.deepEqual(resetGridQuery({ sort: { key: "received", dir: "desc" }, pageSize: 50, page: 4, cursor: "opaque" }, { pageSize: 100 }),
    { sort: { key: "received", dir: "desc" }, pageSize: 100, page: 1, cursor: null });
});

test("preferences render from defaults when storage is absent or unavailable", () => {
  const fallback = { sort: { key: "identifier", dir: "asc" } as const, pageSize: 50 };
  assert.deepEqual(readGridPreference("grid", fallback, null), fallback);
  assert.deepEqual(readGridPreference("grid", fallback, { getItem: () => { throw new Error("blocked"); } }), fallback);
});
