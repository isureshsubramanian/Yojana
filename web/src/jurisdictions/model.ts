import type { RuleContext } from "./types";

export function parseRuleValue(valueType: string, raw: string, language = "en", currency = ""): unknown {
  const clean = raw.trim();
  if (valueType === "boolean") return clean === "true";
  if (["months", "days", "integer"].includes(valueType)) return Number(clean);
  if (valueType === "money") return { amount: Number(clean), currency: currency.trim().toUpperCase() };
  if (valueType === "phrase_list" || valueType === "string_list") return raw.split(/\r?\n/).map((line) => line.trim()).filter(Boolean);
  if (valueType === "text_language") return { text: clean, language: language.trim() || "en", placement: "all", verbatim: true };
  if (valueType === "enum" || valueType === "asset_reference") return clean;
  return JSON.parse(clean) as unknown;
}

export function displayRuleValue(value: unknown): string {
  if (Array.isArray(value)) return value.join("\n");
  if (typeof value === "object" && value !== null && "text" in value) return String((value as { text: unknown }).text);
  if (typeof value === "string") return value;
  return JSON.stringify(value, null, 2);
}

export function buildWhatAppliesRequest(kind: string, context: RuleContext) {
  return { kind: kind || null, context: { ...context, country: context.country.trim().toUpperCase(), subdivision: context.subdivision?.trim().toUpperCase() || null, itemClass: context.itemClass?.trim() || null, currency: context.currency?.trim().toUpperCase() || null } };
}

