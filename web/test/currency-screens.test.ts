import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";

const source = (path: string) => readFileSync(new URL(`../src/${path}`, import.meta.url), "utf8");
const settings = source("settings/Currencies.tsx");
const intake = source("claims/ClaimIntake.tsx");
const claims = source("claims/Claims.tsx");
const api = source("claims/api.ts");

test("currency settings expose one changeable home currency and the read-only upgrade report", () => {
  assert.match(settings, /Home currency/);
  assert.match(settings, /status\.changeable/);
  assert.match(settings, /Locked:/);
  assert.match(settings, /Amounts recorded before this rule/);
  assert.match(settings, /claimsApi\.currencyReport/);
  assert.doesNotMatch(settings, /accepted currencies|acceptedCurrency|toggleAccepted/i);
  assert.match(api, /\/api\/tenant\/currency\/report/);
});

test("claim intake fixes the command currency to the configured home currency", () => {
  assert.match(intake, /<span className="label">Home currency<\/span>/);
  assert.match(intake, /currency,\s*lines:/);
  assert.doesNotMatch(intake, /onChange=\{\(event\) => setCurrency/);
  assert.match(intake, /Claim submission is disabled until then/);
});

test("claim tiles disclose and link every excluded foreign-currency claim", () => {
  assert.match(claims, /otherCurrencyClaimCount/);
  assert.match(claims, /claims?"\} in other currencies are not counted/);
  assert.match(claims, /settings\/currencies#currency-report/);
});
