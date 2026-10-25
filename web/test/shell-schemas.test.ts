import assert from "node:assert/strict";
import test from "node:test";
import * as z from "zod/mini";
import { session } from "../src/api/types.ts";
import { creationOptionsJson, requestOptionsJson } from "../src/security/passkeyApi.ts";
import { pendingSecondStep } from "../src/security/secondStepApi.ts";

// ADR-0019 final batch: sign-in, second step, passkeys, session and shell replies. Values are demo-format, not real data.
const user = { id: "0199a1b2-0000-7000-8000-000000000001", email: "priya.raman@dev.yojana.local", displayName: "Priya Raman" };
const tenants = [{ code: "demo", name: "Demo IT Hardware", roles: ["service_manager"] }];
const signInReply = z.union([pendingSecondStep, session]);

test("a sign-in reply is either a session or a pending second step, and each parses as itself", () => {
  const pending = { secondStepRequired: true, methods: ["authenticator", "recovery_code"], enrolmentRequired: false, userName: "Priya Raman", tenantSummary: tenants };
  const parsedPending = z.parse(signInReply, pending);
  assert.equal("secondStepRequired" in parsedPending, true);
  const signedIn = { user, tenant: { code: "demo", name: "Demo IT Hardware" }, roles: ["service_manager"], tenants, passwordChangeRequired: false };
  const parsedSession = z.parse(signInReply, signedIn);
  assert.equal("user" in parsedSession && parsedSession.user.displayName, "Priya Raman");
  assert.equal(z.safeParse(signInReply, { ...pending, secondStepRequired: false }).success, false);
});

test("passkey ceremony options keep the fields the browser needs, which the client does not name", () => {
  const request = { challenge: "q7Lh", timeout: 60000, rpId: "localhost", allowCredentials: [], userVerification: "preferred", hints: [] };
  assert.deepEqual(z.parse(requestOptionsJson, request), request);
  const creation = {
    challenge: "q7Lh", rp: { id: "localhost", name: "Yojana" }, user: { id: "dXNlcg", name: "priya.raman@dev.yojana.local", displayName: "Priya Raman" },
    pubKeyCredParams: [{ type: "public-key", alg: -7 }], excludeCredentials: [{ type: "public-key", id: "Y3JlZA", transports: ["internal"] }],
    authenticatorSelection: { residentKey: "required" }, attestation: "none",
  };
  assert.deepEqual(z.parse(creationOptionsJson, creation), creation);
  assert.equal(z.safeParse(requestOptionsJson, { ...request, allowCredentials: null }).success, true);
  assert.equal(z.safeParse(requestOptionsJson, { timeout: 60000 }).success, false);
});
