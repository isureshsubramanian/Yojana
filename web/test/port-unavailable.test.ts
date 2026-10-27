import assert from "node:assert/strict";
import test from "node:test";
import { api, ApiError } from "../src/api/client.ts";

test("unavailable port codes use the one connection message", async () => {
  globalThis.fetch = async () => new Response(JSON.stringify({
    title: "Command refused",
    detail: "internal adapter detail",
    rule: "stock_quantity_unavailable",
  }), { status: 409, headers: { "content-type": "application/problem+json" } });

  await assert.rejects(() => api("/api/test"), (reason: unknown) => {
    assert.ok(reason instanceof ApiError);
    assert.equal(reason.detail, "Not available with this connection");
    return true;
  });
});

test("printer setup has its specific action message", async () => {
  globalThis.fetch = async () => new Response(JSON.stringify({
    title: "Command refused",
    rule: "label_printer_not_configured",
  }), { status: 409, headers: { "content-type": "application/problem+json" } });

  await assert.rejects(() => api("/api/test"), (reason: unknown) => {
    assert.ok(reason instanceof ApiError);
    assert.equal(reason.detail, "Label printing is not set up for this site");
    return true;
  });
});
