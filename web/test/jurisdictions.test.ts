import { test } from "node:test";
import assert from "node:assert/strict";
import { buildWhatAppliesRequest, parseRuleValue } from "../src/jurisdictions/model.ts";

test("value editors serialise each catalogue value shape", () => {
  assert.equal(parseRuleValue("months", "12"), 12);
  assert.equal(parseRuleValue("boolean", "true"), true);
  assert.deepEqual(parseRuleValue("money", "49.95", "en", "eur"), { amount: 49.95, currency: "EUR" });
  assert.deepEqual(parseRuleValue("phrase_list", "void warranty\n no cover \n"), ["void warranty", "no cover"]);
  assert.deepEqual(parseRuleValue("text_language", "Keep receipt", "fr"), { text: "Keep receipt", language: "fr", placement: "all", verbatim: true });
  assert.deepEqual(parseRuleValue("json", "{\"threshold\":42}"), { threshold: 42 });
});

test("tester normalises jurisdiction codes and keeps all tested dates", () => {
  const request = buildWhatAppliesRequest("", { country: " de ", subdivision: " de-by ", buyerClass: "consumer", itemClass: " handheld ", price: 499, currency: " eur ", contractDate: "2026-09-01", purchaseDate: null, deliveryDate: "2026-09-03", firstSaleDate: null });
  assert.equal(request.kind, null);
  assert.equal(request.context.country, "DE");
  assert.equal(request.context.subdivision, "DE-BY");
  assert.equal(request.context.currency, "EUR");
  assert.equal(request.context.deliveryDate, "2026-09-03");
});
