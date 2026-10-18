import { test } from "node:test";
import assert from "node:assert/strict";
import { buildWhatAppliesRequest, effectiveRangeLabel, formatRuleValue, pageRules, parseRuleValue, ruleKindLabel, starterName } from "../src/jurisdictions/model.ts";

test("value editors serialise each catalogue value shape", () => {
  assert.equal(parseRuleValue("months", "12"), 12);
  assert.equal(parseRuleValue("boolean", "true"), true);
  assert.deepEqual(parseRuleValue("money", "49.95", "en", "eur"), { amount: 49.95, currency: "EUR" });
  assert.deepEqual(parseRuleValue("phrase_list", "void warranty\n no cover \n"), ["void warranty", "no cover"]);
  assert.deepEqual(parseRuleValue("text_language", "Keep receipt", "fr"), { text: "Keep receipt", language: "fr", placement: "all", verbatim: true });
  assert.deepEqual(parseRuleValue("json", "{\"threshold\":42}"), { threshold: 42 });
});

test("catalogue values are formatted by kind and structured values have labelled parts", () => {
  assert.equal(formatRuleValue("months", 12), "12 months");
  assert.equal(formatRuleValue("boolean", false), "No");
  assert.equal(formatRuleValue("string_list", ["alpha", "beta"]), "alpha, beta");
  assert.equal(formatRuleValue("json", { months: 12, agreement_required: false }), "12 months · agreement not required");
  assert.equal(formatRuleValue("json", { months: 12, requires_agreement: false }), "12 months · agreement not required");
  assert.equal(effectiveRangeLabel("1900-01-01", null, "en-GB"), "always in force");
  assert.equal(effectiveRangeLabel("2026-07-31", null, "en-GB"), "from 31 Jul 2026 · no end date");
  assert.equal(ruleKindLabel("used_goods_min_term"), "Used goods minimum term");
  assert.deepEqual(starterName("European Union (starter — confirm with your legal adviser)"), { name: "European Union", starter: true });
});

test("jurisdiction paging uses the shared one-based page contract", () => {
  const rows = [1, 2, 3, 4];
  assert.deepEqual(pageRules(rows, 1, 25), rows);
  assert.deepEqual(pageRules(Array.from({ length: 30 }, (_, index) => index + 1), 2, 25), [26, 27, 28, 29, 30]);
});

test("tester normalises jurisdiction codes and keeps all tested dates", () => {
  const request = buildWhatAppliesRequest("", { country: " de ", subdivision: " de-by ", buyerClass: "consumer", itemClass: " handheld ", price: 499, currency: " eur ", contractDate: "2026-09-01", purchaseDate: null, deliveryDate: "2026-09-03", firstSaleDate: null });
  assert.equal(request.kind, null);
  assert.equal(request.context.country, "DE");
  assert.equal(request.context.subdivision, "DE-BY");
  assert.equal(request.context.currency, "EUR");
  assert.equal(request.context.deliveryDate, "2026-09-03");
});
