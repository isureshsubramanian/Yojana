import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import { passwordResetApi } from "../src/settings/passwordResetApi.ts";
import { roleLabel, STAFF_ROLES } from "../src/settings/roles.ts";

const users = readFileSync(new URL("../src/settings/Users.tsx", import.meta.url), "utf8");
const accept = readFileSync(new URL("../src/pages/AcceptInvitation.tsx", import.meta.url), "utf8");
const routes = readFileSync(new URL("../src/routes.tsx", import.meta.url), "utf8");

test("Users screen keeps the approved staff-only, KPI, filter, grid and tab structure", () => {
  assert.match(users, /Customer-portal memberships are managed separately and are not listed here/);
  for (const label of ["Active", "Invited", "Deactivated"]) assert.match(users, new RegExp(`label: "${label}"`));
  assert.match(users, /<DataGrid/);
  assert.match(users, /nextCursor/);
  assert.match(users, /Search name or e-mail/);
  assert.match(users, /Filter by role/);
  assert.match(users, /\/settings\/sign-in-policy/);
  assert.match(users, /\/settings\/security-events/);
  assert.equal(roleLabel("certificate_administrator"), "Certificate administrator");
  assert.equal(roleLabel("migration_lead"), "Migration lead");
  assert.ok(STAFF_ROLES.includes("migration_lead"));
});

test("drawer and invitation actions use house dialogs and never browser confirm", () => {
  for (const label of ["Send password reset link", "Set a temporary password", "Deactivate…", "Reactivate", "Resend invitation", "Revoke invitation"])
    assert.match(users, new RegExp(label.replace("…", "…")));
  assert.match(users, /<ActionDialog title="Invite user"/);
  assert.match(users, /already_member/);
  assert.match(users, /already_invited/);
  assert.match(users, /Shown once/);
  assert.match(users, /navigator\.clipboard\.writeText/);
  assert.doesNotMatch(users, /window\.confirm|confirm\(/);
});

test("password-reset client follows the parallel task contract", async () => {
  const calls: Array<{ path: string; method: string; body: string | null }> = [];
  const original = globalThis.fetch;
  globalThis.fetch = (async (input: string | URL | Request, init?: RequestInit) => {
    calls.push({ path: String(input), method: init?.method ?? "GET", body: init?.body ? String(init.body) : null });
    if (String(input).endsWith("password-reset-options")) return new Response(JSON.stringify({ canSendLink: true, canSetTemporary: false, temporaryUnavailableReason: "other_memberships" }), { status: 200 });
    return new Response(JSON.stringify({ mode: "link", mailId: "mail-1", temporaryPassword: null }), { status: 202 });
  }) as typeof fetch;
  try {
    const options = await passwordResetApi.options("user-1");
    assert.equal(options.temporaryUnavailableReason, "other_memberships");
    await passwordResetApi.reset("user-1", "link");
    assert.deepEqual(calls.map((call) => [call.path, call.method]), [
      ["/api/security/users/user-1/password-reset-options", "GET"],
      ["/api/security/users/user-1/password-reset", "POST"],
    ]);
    assert.equal(calls[1]?.body, JSON.stringify({ mode: "link" }));
  } finally { globalThis.fetch = original; }
});

test("accept-invitation reads the fragment and posts only the token body", () => {
  assert.match(routes, /path: "\/accept-invitation"/);
  assert.match(accept, /window\.location\.hash/);
  assert.match(accept, /URLSearchParams/);
  assert.match(accept, /userManagementApi\.inspect\(token\)/);
  assert.match(accept, /userManagementApi\.accept\(token, password\)/);
  assert.match(accept, /userManagementApi\.acceptSignedIn\(token\)/);
  assert.doesNotMatch(accept, /location\.search.*token|\?token=/);
  assert.match(accept, /invitation_other_account/);
  assert.match(accept, /invalid, expired, already used or revoked/);
});
