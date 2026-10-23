import type { JurisdictionRule, RegionGroup, RuleContext } from "./types";
import { countryName } from "../reference/countries.ts";

export interface RegionGroupDraft { code: string; name: string; members: string[] }

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

export function formatRuleValue(valueType: string | undefined, value: unknown, unit?: string | null): string {
  if (value === null || value === undefined) return "—";
  if (valueType === "boolean" || typeof value === "boolean") return value ? "Yes" : "No";
  if (valueType === "months" && typeof value === "number") return `${value} ${value === 1 ? "month" : "months"}`;
  if (valueType === "days" && typeof value === "number") return `${value} ${value === 1 ? "day" : "days"}`;
  if (valueType === "integer" && typeof value === "number") return unit ? `${value} ${value === 1 ? unit.replace(/s$/, "") : unit}` : value.toLocaleString();
  if ((valueType === "phrase_list" || valueType === "string_list") && Array.isArray(value)) return value.map(String).join(", ");
  if (valueType === "money" && isRecord(value)) return `${String(value.amount ?? "—")} ${String(value.currency ?? "")}`.trim();
  if (valueType === "text_language" && isRecord(value)) return String(value.text ?? "—");
  if (Array.isArray(value)) return value.map(String).join(", ");
  if (isRecord(value)) return Object.entries(value).map(([key, part]) => structuredPart(key, part)).join(" · ");
  return humanisePart(String(value));
}

export function effectiveRangeLabel(from: string, to: string | null | undefined, locale?: string): string {
  if (from === "1900-01-01" && !to) return "always in force";
  const start = `from ${displayDate(from, locale)}`;
  return to ? `${start} · until ${displayDate(to, locale)}` : `${start} · no end date`;
}

export function ruleKindLabel(value: string): string {
  if (value === "used_goods_min_term") return "Used goods minimum term";
  return humanisePart(value);
}

export function starterName(value: string): { name: string; starter: boolean } {
  const suffix = /\s*\(starter\s*[—-]\s*confirm with your legal adviser\)\s*$/i;
  return { name: value.replace(suffix, ""), starter: suffix.test(value) };
}

export function pageRules<T>(rows: readonly T[], page: number, pageSize: number): T[] {
  const safePage = Math.max(1, page);
  return rows.slice((safePage - 1) * pageSize, safePage * pageSize);
}

export function scopeDetails(rule: JurisdictionRule, groups: readonly RegionGroup[]): { name: string; code: string | null; short: string; full: string } {
  if (rule.regionGroupId) {
    const group = groups.find((entry) => entry.regionGroupId === rule.regionGroupId);
    const name = group?.name ?? rule.regionGroupName ?? "Region group";
    const code = group?.code ?? null;
    return { name, code, short: code ? `${name} (${code})` : name, full: code ? `${name} (${code})` : name };
  }
  const code = rule.subdivisionCode ?? rule.countryCode;
  if (!code) return { name: "Any", code: null, short: "Any", full: "Any" };
  const name = countryName(code.slice(0, 2));
  return { name, code, short: `${name} (${code})`, full: `${name} (${code})` };
}

export function addRegionMember(members: readonly string[], member: string): string[] {
  const code = member.trim().toUpperCase();
  return code && !members.includes(code) ? [...members, code] : [...members];
}

export function removeRegionMember(members: readonly string[], member: string): string[] {
  return members.filter((entry) => entry !== member);
}

export function buildRegionGroupRequest(commandId: string, draft: RegionGroupDraft) {
  return { commandId, regionGroupId: null, code: draft.code, name: draft.name, members: [...draft.members], expectedVersion: null };
}

function structuredPart(key: string, value: unknown): string {
  if (typeof value === "boolean") {
    const label = humanisePart(key.replace(/^requires_/, "").replace(/_required$/, "")).toLowerCase();
    return value ? `${label} required` : `${label} not required`;
  }
  if (typeof value === "number" && key === "months") return `${value} ${value === 1 ? "month" : "months"}`;
  if (typeof value === "number" && key === "days") return `${value} ${value === 1 ? "day" : "days"}`;
  const rendered = Array.isArray(value) ? value.map(String).join(", ") : isRecord(value) ? formatRuleValue("json", value) : humanisePart(String(value));
  return `${humanisePart(key)}: ${rendered}`;
}

function isRecord(value: unknown): value is Record<string, unknown> { return typeof value === "object" && value !== null && !Array.isArray(value); }
function humanisePart(value: string): string { return value.replaceAll("_", " ").replace(/^./, (letter) => letter.toUpperCase()); }
function displayDate(value: string, locale?: string): string {
  return new Intl.DateTimeFormat(locale, { day: "numeric", month: "short", year: "numeric", timeZone: "UTC" })
    .format(new Date(`${value}T00:00:00Z`));
}

export function buildWhatAppliesRequest(kind: string, context: RuleContext) {
  return { kind: kind || null, context: { ...context, country: context.country.trim().toUpperCase(), subdivision: context.subdivision?.trim().toUpperCase() || null, itemClass: context.itemClass?.trim() || null, currency: context.currency?.trim().toUpperCase() || null } };
}

