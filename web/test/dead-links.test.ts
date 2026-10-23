import assert from "node:assert/strict";
import { readdirSync, readFileSync } from "node:fs";
import { extname, join, relative } from "node:path";
import { fileURLToPath } from "node:url";
import test from "node:test";

const sourceRoot = fileURLToPath(new URL("../src", import.meta.url));
const routesSource = readFileSync(join(sourceRoot, "routes.tsx"), "utf8");
const registrySource = readFileSync(join(sourceRoot, "settings/registry.ts"), "utf8");
const serverPaths = new Set(["/health", "/notices"]);

function sourceFiles(directory: string): string[] {
  return readdirSync(directory, { withFileTypes: true }).flatMap(entry => {
    const path = join(directory, entry.name);
    return entry.isDirectory() ? sourceFiles(path) : [".ts", ".tsx"].includes(extname(path)) ? [path] : [];
  });
}

function routeLiterals(source: string): string[] {
  return [...source.matchAll(/\bpath:\s*"([^"]+)"/g)].map(match => match[1]!);
}

function routePattern(path: string): RegExp {
  const clean = path.startsWith("/") ? path : `/${path}`;
  const parts = clean.split("/").map(part => part === "*" ? ".*" : part.startsWith(":") ? "[^/]+" : part.replace(/[.*+?^${}()|[\]\\]/g, "\\$&"));
  return new RegExp(`^${parts.join("/")}/?$`);
}

test("every static internal link resolves to a declared client route", () => {
  const portalStart = routesSource.indexOf('path: "portal"');
  const portalEnd = routesSource.indexOf("Component: Frame", portalStart);
  assert.ok(portalStart >= 0 && portalEnd > portalStart, "portal route block was not found");

  const portalSource = routesSource.slice(portalStart, portalEnd);
  const routesWithoutPortalChildren = routesSource.slice(0, portalStart) + routesSource.slice(portalEnd);
  const declared = [
    ...routeLiterals(routesWithoutPortalChildren).filter(path => path !== "*"),
    "/portal",
    ...routeLiterals(portalSource).filter(path => path !== "portal").map(path => `/portal/${path}`),
    ...[...registrySource.matchAll(/\bpath:\s*"(\/settings\/[^"]+)"/g)].map(match => match[1]!),
  ].map(routePattern);

  const failures: string[] = [];
  for (const file of sourceFiles(sourceRoot)) {
    const source = readFileSync(file, "utf8");
    for (const match of source.matchAll(/\b(?:to|href)\s*=\s*(?:"(\/[^"{}]*)"|'(\/[^'{}]*)'|\{\s*"(\/[^"{}]*)"\s*\}|\{\s*'(\/[^'{}]*)'\s*\})/g)) {
      const link = match.slice(1).find(Boolean)!;
      const path = link.split(/[?#]/, 1)[0]!;
      if (!serverPaths.has(path) && !declared.some(pattern => pattern.test(path)))
        failures.push(`${relative(sourceRoot, file)}: ${link}`);
    }
  }
  assert.deepEqual(failures, []);
});
