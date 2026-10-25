import assert from "node:assert/strict";
import test from "node:test";
import { securityEventKinds, securityEventLabel, securityEventTone } from "../src/settings/securityEventsModel.ts";

test("API client security events have dedicated readable labels", () => {
  const expected = {
    api_token_issued: "API token issued",
    api_token_refused: "API token refused",
    api_client_created: "API client created",
    api_client_secret_rotated: "API client secret rotated",
    api_client_revoked: "API client revoked",
    api_client_changed: "API client changed",
  } as const;

  for (const [kind, label] of Object.entries(expected)) {
    assert.ok(securityEventKinds.includes(kind as typeof securityEventKinds[number]));
    assert.equal(securityEventLabel(kind), label);
  }
  assert.equal(securityEventTone("api_token_refused"), "bad");
});
