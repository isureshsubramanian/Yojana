import { api, ApiError } from "../api/client";
import type {
  CertificateDetail, CertificateListRow, CertificateTemplateDetail, CertificateTemplateSummary,
  CertificateVersionChain, FieldCatalogueEntry, LabelTemplateDetail, LabelTemplateSummary, Page,
  TemplateValidation, TenantAsset,
} from "./types";

export const certificateApi = {
  list: (params: URLSearchParams, signal?: AbortSignal) => api<Page<CertificateListRow>>(`/api/certificates/?${params}`, { signal }),
  detail: (id: string, signal?: AbortSignal) => api<CertificateDetail>(`/api/certificates/${id}`, { signal }),
  chain: (unitId: string, signal?: AbortSignal) => api<CertificateVersionChain>(`/api/certificates/units/${unitId}/versions`, { signal }),
  templates: (signal?: AbortSignal) => api<CertificateTemplateSummary[]>("/api/certificate-templates/", { signal }),
  template: (id: string, signal?: AbortSignal) => api<CertificateTemplateDetail>(`/api/certificate-templates/${id}`, { signal }),
  fields: (kind: string, signal?: AbortSignal) => api<FieldCatalogueEntry[]>(`/api/certificate-templates/field-catalogue/${kind}`, { signal }),
  createTemplate: (body: unknown) => api<CertificateTemplateDetail>("/api/certificate-templates/", { method: "POST", body }),
  saveDraft: (id: string, body: unknown) => api<{ version: CertificateTemplateDetail["versions"][number]; validation: TemplateValidation }>(`/api/certificate-templates/${id}/versions/draft`, { method: "POST", body }),
  validate: (id: string, body: unknown) => api<TemplateValidation>(`/api/certificate-templates/${id}/validate`, { method: "POST", body }),
  activate: (id: string, versionId: string) => api<CertificateTemplateDetail["versions"][number]>(`/api/certificate-templates/${id}/versions/${versionId}/activate`, { method: "POST", body: {} }),
  retire: (id: string, versionId: string) => api<CertificateTemplateDetail["versions"][number]>(`/api/certificate-templates/${id}/versions/${versionId}/retire`, { method: "POST", body: {} }),
  preview: (id: string, versionId: string, sampleUnitId: string) => api<{ bytes: string; mediaType: string; validation: TemplateValidation }>(`/api/certificate-templates/${id}/versions/${versionId}/preview`, { method: "POST", body: { sampleUnitId } }),
  labels: (signal?: AbortSignal) => api<LabelTemplateSummary[]>("/api/labels/templates?take=200", { signal }),
  label: (id: string, signal?: AbortSignal) => api<LabelTemplateDetail>(`/api/labels/templates/${id}`, { signal }),
  assets: (signal?: AbortSignal) => api<TenantAsset[]>("/api/tenant-assets/", { signal }),
  async uploadAsset(file: File, legalEntityId: string | null): Promise<TenantAsset> {
    const body = new FormData(); body.append("commandId", crypto.randomUUID()); body.append("file", file); if (legalEntityId) body.append("legalEntityId", legalEntityId);
    const response = await fetch("/api/tenant-assets/", { method: "POST", credentials: "same-origin", body });
    if (!response.ok) {
      let detail = response.statusText || `HTTP ${response.status}`;
      try { const problem = await response.json() as { detail?: string; title?: string }; detail = problem.detail ?? problem.title ?? detail; } catch { /* response was not JSON */ }
      throw new ApiError(response.status, detail);
    }
    return await response.json() as TenantAsset;
  },
};
