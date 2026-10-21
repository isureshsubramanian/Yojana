import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import { companyLine } from "../src/settings/companyApi.ts";

const source = (path: string) => readFileSync(new URL(`../src/${path}`, import.meta.url), "utf8");
const company = source("settings/Company.tsx");
const policy = source("settings/SignInPolicy.tsx");
const settings = source("settings/Settings.tsx");
const routes = source("routes.tsx");

test("the footer preview composes the line the server writes", () => {
  assert.equal(companyLine({ companyName: "Asterra Compute Works", footerLine: " Service operations · Bengaluru " }),
    "Asterra Compute Works · Service operations · Bengaluru");
  assert.equal(companyLine({ companyName: "Asterra Compute Works", footerLine: "  " }), "Asterra Compute Works");
});

test("Settings › Company edits the footer line, sender and reply-to, and offers no logo", () => {
  assert.match(settings, /"\/settings\/company"/);
  assert.match(routes, /settings\/company/);
  assert.match(company, /maxLength=\{120\}/);
  assert.match(company, /Send a test message to me/);
  assert.doesNotMatch(company, /type="file"/);
});

test("the sign-in policy sets invitation expiry from 1 to 30 days", () => {
  assert.match(policy, /Invitations work for/);
  assert.match(policy, /min=\{1\} max=\{30\}/);
});
