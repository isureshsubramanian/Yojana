import assert from "node:assert/strict";
import { readdirSync, readFileSync, statSync } from "node:fs";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

import { handoff, opaqueParam } from "../src/shell/handoff.ts";

const root = fileURLToPath(new URL("../src", import.meta.url));
const files = (dir: string): string[] => readdirSync(dir).flatMap((name) => {
  const path = join(dir, name);
  return statSync(path).isDirectory() ? files(path) : /\.tsx?$/.test(name) ? [path] : [];
});

test("screen links never carry serials, scanned identifiers or record numbers in the query string", () => {
  // Browser URLs only: /api calls are server requests and are not scanned here. Values ending in "Id" are record ids.
  // Search-box terms (?q=, ?serial=) are a separate owner decision and are not covered.
  const readable = /[?&](?:serials|resolved|first|second|claim)=\$\{encodeURIComponent\((?![^)]*Id\b)/;
  const offenders = files(root).flatMap((file) => readFileSync(file, "utf8").split("\n")
    .flatMap((line, index) => readable.test(line) && !line.includes("/api/") ? [`${file.slice(root.length)}:${index + 1}`] : []));
  assert.deepEqual(offenders, []);
});

test("hand-off state keeps only non-empty strings for its known keys", () => {
  assert.deepEqual(handoff(null), {});
  assert.deepEqual(handoff("serials"), {});
  assert.deepEqual(handoff({ serials: "SN-1\nSN-2", entry: "", resolvedFrom: 4, other: "x" }), { serials: "SN-1\nSN-2" });
});

test("opaque query values accept record ids and ignore typed serials", () => {
  const params = new URLSearchParams("first=0199a1b2-7c3d-7e4f-8a5b-6c7d8e9f0a1b&second=SRV-2510-007713");
  assert.equal(opaqueParam(params, "first"), "0199a1b2-7c3d-7e4f-8a5b-6c7d8e9f0a1b");
  assert.equal(opaqueParam(params, "second"), "");
  assert.equal(opaqueParam(params, "missing"), "");
});
