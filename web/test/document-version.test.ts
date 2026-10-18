import assert from "node:assert/strict";
import test from "node:test";
import { ApiError, api } from "../src/api/client.ts";

test("document-changed refusal keeps actor, time and reload instruction", async () => {
  const originalFetch = globalThis.fetch;
  globalThis.fetch = async () => new Response(JSON.stringify({
    title: "document_changed",
    detail: "This document changed after it was opened.",
    rule: "document_changed",
    changedBy: "user-42",
    changedAt: "2026-09-01T09:30:00Z",
    unblock: "Reload to see their change",
  }), { status: 409, headers: { "content-type": "application/problem+json" } });

  try {
    let error: ApiError | null = null;
    try {
      await api("/api/campaigns/one");
      assert.fail("the request should have been refused");
    } catch (reason) {
      assert.ok(reason instanceof ApiError);
      error = reason;
    }
    assert.ok(error);
    assert.equal(error.status, 409);
    assert.equal(error.rule, "document_changed");
    assert.equal(error.changedBy, "user-42");
    assert.equal(error.changedAt, "2026-09-01T09:30:00Z");
    assert.equal(error.unblockingStep, "Reload to see their change");
  } finally {
    globalThis.fetch = originalFetch;
  }
});
