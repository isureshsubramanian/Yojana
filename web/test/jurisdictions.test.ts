import { test } from "node:test";
import assert from "node:assert/strict";
import { createRequire } from "node:module";
import { fileURLToPath } from "node:url";
import { build } from "esbuild";
import { addRegionMember, buildRegionGroupRequest, buildWhatAppliesRequest, effectiveRangeLabel, formatRuleValue, pageRules, parseRuleValue, ruleKindLabel, scopeDetails, starterName } from "../src/jurisdictions/model.ts";
import type { JurisdictionRule } from "../src/jurisdictions/types.ts";
import { countryLabel, filterCountryOptions } from "../src/reference/countries.ts";
import { isSubdivisionCode, subdivisionParts } from "../src/reference/subdivisions.ts";

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

test("country picker filters localised names and selection stores the alpha-2 code", async () => {
  assert.equal(countryLabel("IN"), "India (IN)");
  assert.ok(filterCountryOptions("ind").some((option) => option.code === "IN"));
  const picker = await interactiveCountryPicker();
  const selected: string[] = [];
  let nodes = elements(picker.render({ id: "country", label: "Country", value: "", onChange: (code: string) => selected.push(code) }));
  nodes.find((node) => node.props?.role === "combobox").props.onChange({ target: { value: "ind" } });
  nodes = elements(picker.render({ id: "country", label: "Country", value: "", onChange: (code: string) => selected.push(code) }));
  const india = nodes.find((node) => node.props?.role === "option" && textOf(node) === "India (IN)");
  assert.ok(india); india.props.onClick();
  assert.deepEqual(selected, ["IN"]);
});

test("subdivision builder parses, assembles and validates ISO 3166-2 syntax", () => {
  assert.deepEqual(subdivisionParts("in-ka"), { country: "IN", suffix: "KA" });
  assert.equal(isSubdivisionCode("IN-KA"), true);
  assert.equal(isSubdivisionCode("IN-"), false);
  assert.equal(isSubdivisionCode("IN-KARN"), false);
  assert.equal(isSubdivisionCode("XX-KA"), false);
});

test("region-group country chips round-trip as the save request members array", () => {
  let members = addRegionMember([], "in");
  members = addRegionMember(members, "IN-KA");
  members = addRegionMember(members, "IN");
  const request = buildRegionGroupRequest("command-1", { code: "SOUTH", name: "Southern service", members });
  assert.deepEqual(request.members, ["IN", "IN-KA"]);
});

test("rule scopes display localised country names with their codes", () => {
  const rule = { countryCode: "IN", subdivisionCode: null, regionGroupId: null, regionGroupName: null } as JurisdictionRule;
  assert.deepEqual(scopeDetails(rule, []), { name: "India", code: "IN", short: "India (IN)", full: "India (IN)" });
  assert.deepEqual(scopeDetails({ ...rule, countryCode: null, subdivisionCode: "IN-KA" }, []), { name: "India", code: "IN-KA", short: "India (IN-KA)", full: "India (IN-KA)" });
});

async function interactiveCountryPicker() {
  const react = createRequire(import.meta.url)("react");
  const slots: unknown[] = []; let cursor = 0;
  const hooks = {
    ...react,
    useState(initial: unknown) { const index = cursor++; if (!(index in slots)) slots[index] = typeof initial === "function" ? (initial as () => unknown)() : initial; return [slots[index], (value: unknown) => { slots[index] = typeof value === "function" ? (value as (current: unknown) => unknown)(slots[index]) : value; }]; },
    useId() { return "country-fixture"; },
  };
  const result = await build({ entryPoints: [fileURLToPath(new URL("../src/reference/CountrySelect.tsx", import.meta.url))], bundle: true, platform: "node", format: "cjs", jsx: "automatic", packages: "external", loader: { ".css": "empty" }, write: false });
  const module = { exports: {} as Record<string, (props: Record<string, unknown>) => unknown> };
  const require = createRequire(import.meta.url);
  new Function("require", "module", "exports", result.outputFiles[0]!.text)((name: string) => name === "react" ? hooks : require(name), module, module.exports);
  return { render(props: Record<string, unknown>) { cursor = 0; return module.exports.CountrySelect!(props); } };
}

function elements(tree: any): any[] {
  if (!tree || typeof tree !== "object") return [];
  if (Array.isArray(tree)) return tree.flatMap(elements);
  return [tree, ...elements(tree.props?.children)];
}

function textOf(tree: any): string {
  if (typeof tree === "string") return tree;
  if (Array.isArray(tree)) return tree.map(textOf).join("");
  return tree && typeof tree === "object" ? textOf(tree.props?.children) : "";
}

test("currency choices are ISO codes with names and keep an unlisted saved value", async () => {
  const { currencyOptions } = await import("../src/reference/currencies.ts");
  const options = currencyOptions("XTS");
  assert.ok(options.some((option) => option.code === "EUR" && option.label.startsWith("EUR · ")));
  assert.ok(options.some((option) => option.code === "XTS"));
  assert.deepEqual(options.map((option) => option.code), [...options.map((option) => option.code)].sort());
});
