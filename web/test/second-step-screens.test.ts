import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";

const source = (path: string) => readFileSync(new URL(`../src/${path}`, import.meta.url), "utf8");
const signIn = source("pages/SignIn.tsx"); const account = source("pages/AccountSecurity.tsx");
const policy = source("settings/SignInPolicy.tsx"); const users = source("settings/Users.tsx");

test("sign-in renders authenticator, recovery and required enrolment without passkeys", () => {
  for (const phrase of ["Enter the code from your authenticator app", "Use a recovery code", "Lost your phone?", "Set up an authenticator app", "Manual key", "I have saved these codes", "Download .txt"]) assert.match(signIn, new RegExp(phrase.replace("?", "\\?")));
  assert.match(signIn, /secondStepApi\.complete/); assert.match(signIn, /secondStepApi\.confirm/);
  assert.doesNotMatch(signIn, /passkey/i);
});

test("account security has the approved second-step card and one-time code controls", () => {
  for (const phrase of ["Second step", "Required for your role", "Replace authenticator…", "New recovery codes…", "Turn off second step", "You cannot turn the second step off", "shown once", "Copy", "Download .txt"]) assert.match(account, new RegExp(phrase));
  assert.match(account, /old authenticator keeps working until the new code is verified/);
  assert.doesNotMatch(account, /passkey|Recent sign-ins/i);
});

test("policy and Users extend their existing screens with second-step state", () => {
  for (const phrase of ["Second step by role", "required", "optional", "off", "A person with several roles follows the strictest", "Show all roles", "have not set one up"]) assert.match(policy, new RegExp(phrase));
  for (const phrase of ["Second step on", "Required but not set up", "No second step", "Sign-in methods", "Authenticator app", "recovery codes left", "Reset second step…"]) assert.match(users, new RegExp(phrase));
  assert.match(users, /resetSecondStep/); assert.doesNotMatch(users, /window\.confirm|confirm\(/);
});
