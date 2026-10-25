import * as z from "zod/mini";
import { apiParsed, checkedBody } from "../api/client.ts";
import { catalogueSchema, packPreviewSchema, ruleContextSchema, ruleResolutionSchema, type RuleContext } from "./types.ts";

const root = "/api/settings/jurisdictions";
const text = z.string();
const regionGroupSavedSchema = z.object({ regionGroupId: text, version: text, replayed: z.boolean() });
const jurisdictionRuleSavedSchema = z.object({ ruleId: text, rootRuleId: text, version: z.number(), versionToken: text, replayed: z.boolean() });
const jurisdictionRuleClosedSchema = z.object({ ruleId: text, effectiveTo: text, versionToken: text, replayed: z.boolean() });
const rulePackImportedSchema = z.object({ rulePackId: text, regionGroupsAdded: z.number(), rulesAdded: z.number(), replayed: z.boolean() });
const importPackRequestSchema = z.object({ commandId: text, rulePackId: text });
const whatAppliesRequestSchema = z.object({ kind: z.nullable(text), context: ruleContextSchema });
export const jurisdictionApi = {
  catalogue: (country: string, kind: string, signal?: AbortSignal) => apiParsed(`${root}?country=${encodeURIComponent(country)}&kind=${encodeURIComponent(kind)}`, catalogueSchema, { signal }),
  saveGroup: (body: unknown) => apiParsed(root + "/region-groups", regionGroupSavedSchema, { method: "POST", body }),
  saveRule: (body: unknown) => apiParsed(root + "/rules", jurisdictionRuleSavedSchema, { method: "POST", body }),
  closeRule: (id: string, body: unknown) => apiParsed(`${root}/rules/${id}/close`, jurisdictionRuleClosedSchema, { method: "POST", body }),
  packPreview: (id: string) => apiParsed(`${root}/packs/${id}/preview`, packPreviewSchema),
  importPack: (id: string) => { const body = { commandId: crypto.randomUUID(), rulePackId: id }; return apiParsed(`${root}/packs/${id}/import`, rulePackImportedSchema, { method: "POST", body: checkedBody(importPackRequestSchema, body) }); },
  whatApplies: (kind: string, context: RuleContext) => { const body = { kind: kind || null, context }; return apiParsed(root + "/what-applies", z.object({ rules: z.array(ruleResolutionSchema) }), { method: "POST", body: checkedBody(whatAppliesRequestSchema, body) }); },
};

