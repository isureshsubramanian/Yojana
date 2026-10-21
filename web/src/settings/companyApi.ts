import { api } from "../api/client.ts";

export interface CompanyMailSettings {
  companyName: string;
  footerLine: string;
  senderName: string;
  replyTo: string;
  version: string;
}

export const companyApi = {
  mail: (signal?: AbortSignal) => api<CompanyMailSettings>("/api/platform/company/mail", signal ? { signal } : undefined),
  saveMail: (settings: CompanyMailSettings) => api<CompanyMailSettings>("/api/platform/company/mail", { method: "PUT", body: settings }),
  sendTest: () => api<{ mailId: string }>("/api/platform/mail/test", { method: "POST", body: {} }),
};

/** "Name · footer line", as every e-mail's footer shows it (CompanyMailIdentity.Compose on the server). */
export function companyLine(settings: Pick<CompanyMailSettings, "companyName" | "footerLine">): string {
  const line = settings.footerLine.trim();
  return line ? `${settings.companyName} · ${line}` : settings.companyName;
}
