import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import { tokenFromHash } from "../src/security/resetModel.ts";
import { DEFAULT_PASSWORD_RULES, minimumLengthValid, ruleChecks } from "../src/security/passwordRulesModel.ts";

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
  assert.match(rules, /ruleChecks\(password, rules\)/);
  assert.match(rules, /Not a common or easy-to-guess password/);
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
  assert.match(account, /id="account-passkeys">Passkeys/);
  assert.match(policy, /id="password-policy">Passwords/);
  assert.match(policy, /id="temporary-password-policy">Temporary passwords/);
  assert.match(policy, /Second step by role/);
  assert.match(policy, /id="passkey-policy">Passkeys/); assert.doesNotMatch(policy, /Failed attempts/);
  assert.match(policy, /min=\{10\} max=\{1440\}/);
});

test("password hints follow the company's rules and tick as each one is met", () => {
  const labels = (password: string, rules = DEFAULT_PASSWORD_RULES) => ruleChecks(password, rules).map((check) => `${check.key}:${check.met}`);
  assert.deepEqual(labels(""), ["length:false", "upper:false", "lower:false", "digit:false", "symbol:false"]);
  assert.deepEqual(labels("Brass Kettle 47"), ["length:true", "upper:true", "lower:true", "digit:true", "symbol:true"]);
  assert.deepEqual(labels("kettle", { minimumLength: 6, requireUppercase: false, requireLowercase: true, requireDigit: false, requireSymbol: false }), ["length:true", "lower:true"]);
  assert.deepEqual(labels("தமிழ் 2026", { ...DEFAULT_PASSWORD_RULES, minimumLength: 8 }), ["length:true", "upper:true", "lower:true", "digit:true", "symbol:true"]);
  assert.equal(ruleChecks("x", { ...DEFAULT_PASSWORD_RULES, minimumLength: 20 })[0]!.label, "At least 20 characters");
  assert.deepEqual([7, 8, 64, 65, 12.5].map(minimumLengthValid), [false, true, true, false, false]);
});

test("the rules read posts its token in the body and every set-password screen uses it", () => {
  assert.match(api, /"\/api\/auth\/password-rules", passwordRuleSet, \{ method: "POST", body: whose/);
  assert.match(reset, /usePasswordRules\(token \? \{ reset: token \}/);
  assert.match(source("pages/AcceptInvitation.tsx"), /usePasswordRules\(token \? \{ invitation: token \}/);
  assert.match(source("security/PasswordChangeForm.tsx"), /usePasswordRules\(\{\}\)/);
  assert.match(policy, /Must include/);
  assert.match(policy, /Common and easy-to-guess passwords are always refused\./);
  assert.doesNotMatch(policy, /Set for the whole installation/);
});
