import { api } from "../api/client";
import type { Catalogue, PackPreview, RuleContext, RuleResolution } from "./types";

const root = "/api/settings/jurisdictions";
export const jurisdictionApi = {
  catalogue: (country: string, kind: string, signal?: AbortSignal) => api<Catalogue>(`${root}?country=${encodeURIComponent(country)}&kind=${encodeURIComponent(kind)}`, { signal }),
  saveGroup: (body: unknown) => api(root + "/region-groups", { method: "POST", body }),
  saveRule: (body: unknown) => api(root + "/rules", { method: "POST", body }),
  closeRule: (id: string, body: unknown) => api(`${root}/rules/${id}/close`, { method: "POST", body }),
  packPreview: (id: string) => api<PackPreview>(`${root}/packs/${id}/preview`),
  importPack: (id: string) => api(`${root}/packs/${id}/import`, { method: "POST", body: { commandId: crypto.randomUUID(), rulePackId: id } }),
  whatApplies: (kind: string, context: RuleContext) => api<{ rules: RuleResolution[] }>(root + "/what-applies", { method: "POST", body: { kind: kind || null, context } }),
};

