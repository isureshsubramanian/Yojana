import { api } from "../api/client";
import type { CertificateDateFormatChoice } from "./certificateDateFormats";

export type ReleaseRecordIssuePoint = "at_first_shipment" | "at_build_release" | "on_request";

export interface ReleaseRecordIssuePointOverride {
  itemClass: string;
  issuePoint: ReleaseRecordIssuePoint;
}

export interface ReleaseRecordIssuePointSettings {
  defaultIssuePoint: ReleaseRecordIssuePoint;
  itemClassOverrides: ReleaseRecordIssuePointOverride[];
  version: string;
}

export interface CertificateDateFormatSettings {
  languages: CertificateDateFormatChoice[];
  version: string;
}

export const certificateSettingsApi = {
  get: (signal?: AbortSignal | undefined) => api<ReleaseRecordIssuePointSettings>(
    "/api/certificates/settings/release-record-issue-point", { signal }),
  save: (settings: ReleaseRecordIssuePointSettings) => api<ReleaseRecordIssuePointSettings>(
    "/api/certificates/settings/release-record-issue-point", {
      method: "PUT",
      body: {
        defaultIssuePoint: settings.defaultIssuePoint,
        itemClassOverrides: settings.itemClassOverrides,
        expectedVersion: settings.version,
      },
    }),
  getDateFormats: (signal?: AbortSignal | undefined) => api<CertificateDateFormatSettings>(
    "/api/certificates/settings/date-format", { signal }),
  saveDateFormats: (settings: CertificateDateFormatSettings) => api<CertificateDateFormatSettings>(
    "/api/certificates/settings/date-format", {
      method: "PUT",
      body: { languages: settings.languages, expectedVersion: settings.version },
    }),
};
