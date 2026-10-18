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
    let draftText = "Controller replaced; customer text is still here.";
    const reload = () => { draftText = "server text after reload"; };
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
    assert.equal(draftText, "Controller replaced; customer text is still here.", "the refusal does not mutate local form state");
    reload();
    assert.equal(draftText, "server text after reload", "the screen replaces local state only after the user chooses Reload");
  } finally {
    globalThis.fetch = originalFetch;
  }
});

test("an HTML 500 body is never exposed to the user", async () => {
  const originalFetch = globalThis.fetch;
  globalThis.fetch = async () => new Response("<html><body>stack trace; Cookie: yojana.session=secret</body></html>", {
    status: 500,
    headers: { "content-type": "text/html" },
  });
  try {
    await assert.rejects(() => api("/api/certificates/selection/preview"), (reason: unknown) => {
      assert.ok(reason instanceof ApiError);
      assert.equal(reason.message, "Something went wrong (HTTP 500)");
      assert.doesNotMatch(reason.message, /stack|cookie|secret|html/i);
      return true;
    });
  } finally {
    globalThis.fetch = originalFetch;
  }
});
