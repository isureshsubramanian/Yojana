import assert from "node:assert/strict";
import test from "node:test";
import * as z from "zod/mini";
import { ApiError, apiParsed, unexpectedResponseRule } from "../src/api/client.ts";

// ADR-0019 Q3: a matching response is returned without unknown fields; a mismatch fails closed, and the console
// names where the response differed but never a value it held.
const schema = z.object({ id: z.string(), count: z.number(), note: z.nullable(z.string()) });

async function withResponse<T>(body: unknown, run: (warnings: string[]) => Promise<T>): Promise<T> {
  const originalFetch = globalThis.fetch;
  const originalWarn = console.warn;
  const warnings: string[] = [];
  globalThis.fetch = (async () => new Response(JSON.stringify(body), { status: 200 })) as typeof fetch;
  console.warn = (...parts: unknown[]) => { warnings.push(parts.map(String).join(" ")); };
  try { return await run(warnings); } finally { globalThis.fetch = originalFetch; console.warn = originalWarn; }
}

test("a matching response is returned typed, with fields the schema does not know dropped", async () => {
  await withResponse({ id: "opaque-1", count: 3, note: null, addedLater: "ignored" }, async warnings => {
    const value = await apiParsed("/api/example?cursor=abc", schema);
    assert.deepEqual(value, { id: "opaque-1", count: 3, note: null });
    assert.deepEqual(warnings, []);
  });
});

test("a mismatch throws the unexpected-response ApiError and logs the path but never the value", async () => {
  await withResponse({ id: "Fictional Customer Ltd", count: "SERIAL-PRIVATE-77", note: 5 }, async warnings => {
    await assert.rejects(apiParsed("/api/example?cursor=secret-cursor", schema), (reason: unknown) => {
      assert.ok(reason instanceof ApiError);
      assert.equal(reason.rule, unexpectedResponseRule);
      assert.match(reason.message, /unexpected response/);
      return true;
    });
    assert.equal(warnings.length, 1);
    const [warning = ""] = warnings;
    assert.match(warning, /count invalid_type/);
    assert.match(warning, /note invalid_type/);
    for (const leaked of ["SERIAL-PRIVATE-77", "Fictional Customer Ltd", "secret-cursor", "5"])
      assert.ok(!warning.includes(leaked), `warning must not contain ${leaked}`);
  });
});

test("a schema that is missing a required field fails closed", async () => {
  await withResponse({ id: "opaque-2", note: null }, async () => {
    await assert.rejects(apiParsed("/api/example", schema), (reason: unknown) => reason instanceof ApiError);
  });
});
