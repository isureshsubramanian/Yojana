import { api } from "../api/client";
import { newCommandId, type ResolveSelectionSetResult, type SelectionSource } from "../api/units";
import type { CertificateRunResult, CertificateSelectionPreview, CertificateSelectionPreviewRequest, IssueCertificateRequest } from "./types";

export const certificateIssueApi = {
  resolve: (source: SelectionSource) => api<ResolveSelectionSetResult>("/api/selections/", { method: "POST", body: { commandId: newCommandId(), source } }),
  preview: (request: CertificateSelectionPreviewRequest, signal?: AbortSignal) => api<CertificateSelectionPreview>("/api/certificates/selection/preview", { method: "POST", body: request, signal }),
  issue: (request: IssueCertificateRequest) => api<CertificateRunResult>("/api/certificates/selection/issue", { method: "POST", body: request }),
};

export function downloadArchive(result: CertificateRunResult): void {
  const binary = atob(result.archive);
  const bytes = new Uint8Array(binary.length);
  for (let index = 0; index < binary.length; index += 1) bytes[index] = binary.charCodeAt(index);
  const url = URL.createObjectURL(new Blob([bytes], { type: result.archiveMediaType }));
  const link = document.createElement("a"); link.href = url; link.download = result.archiveFileName; link.click();
  window.setTimeout(() => URL.revokeObjectURL(url), 0);
}
