import * as z from "zod/mini";
import { apiParsed } from "../api/client.ts";

export const companyMailSettings = z.object({ companyName: z.string(), footerLine: z.string(), senderName: z.string(),
  replyTo: z.string(), version: z.string() });
export type CompanyMailSettings = z.infer<typeof companyMailSettings>;

export const companyApi = {
  mail: (signal?: AbortSignal) => apiParsed("/api/platform/company/mail", companyMailSettings, signal ? { signal } : undefined),
  saveMail: (settings: CompanyMailSettings) => apiParsed("/api/platform/company/mail", companyMailSettings, { method: "PUT", body: settings }),
  sendTest: () => apiParsed("/api/platform/mail/test", z.object({ mailId: z.string() }), { method: "POST", body: {} }),
};

/** "Name · footer line", as every e-mail's footer shows it (CompanyMailIdentity.Compose on the server). */
export function companyLine(settings: Pick<CompanyMailSettings, "companyName" | "footerLine">): string {
  const line = settings.footerLine.trim();
  return line ? `${settings.companyName} · ${line}` : settings.companyName;
}
