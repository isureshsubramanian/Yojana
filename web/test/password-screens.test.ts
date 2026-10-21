import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import { tokenFromHash } from "../src/security/resetModel.ts";

const source = (path: string) => readFileSync(new URL(`../src/${path}`, import.meta.url), "utf8");
const signIn = source("pages/SignIn.tsx");
const forgot = source("pages/ForgotPassword.tsx");
const reset = source("pages/ResetPassword.tsx");
const change = source("pages/ChangePassword.tsx");
const account = source("pages/AccountSecurity.tsx");
const policy = source("settings/SignInPolicy.tsx");
const api = source("security/api.ts");

test("sign-in links to the neutral forgot-password flow", () => {
  assert.match(signIn, /href="\/forgot-password"/);
  assert.match(forgot, /Forgot your password\?/);
  assert.match(forgot, /If an account exists for \{sentTo\}, we sent a link/);
  assert.match(api, /password-reset\/request/);
});

test("reset reads only the fragment token and posts it in the JSON body", () => {
  assert.equal(tokenFromHash("#t=abc_DEF-123"), "abc_DEF-123");
  assert.equal(tokenFromHash("#x=abc"), null);
  assert.match(reset, /tokenFromHash\(window\.location\.hash\)/);
  assert.match(api, /body: \{ token, newPassword \}/);
  assert.doesNotMatch(api, /password-reset\/complete\?/);
  assert.match(reset, /This link has expired/);
  assert.match(reset, /Request a new link/);
});

test("new-password screens show the live approved rules and server policy reasons", () => {
  const rules = source("security/PasswordRules.tsx");
  assert.match(rules, /At least 12 characters/);
  assert.match(rules, /Not your e-mail or your name/);
  assert.match(rules, /Both entries match/);
  assert.match(reset, /reason\.message/);
  assert.match(reset, /Save and sign in/);
  assert.match(change, /PasswordChangeForm/);
});

test("mandatory change, account security and sign-in policy include their completed task sections", () => {
  assert.match(change, /temporary password worked/);
  assert.match(account, /id="account-password">Password/);
  assert.match(account, /Second step/);
  assert.match(account, /Recovery codes/);
  assert.doesNotMatch(account, /Passkey/);
  assert.match(policy, /id="password-policy">Passwords/);
  assert.match(policy, /id="temporary-password-policy">Temporary passwords/);
  assert.match(policy, /Second step by role/);
  assert.doesNotMatch(policy, /Passkeys|Failed attempts/);
  assert.match(policy, /min=\{10\} max=\{1440\}/);
});
