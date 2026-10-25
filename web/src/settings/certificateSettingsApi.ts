import * as z from "zod/mini";
import { apiParsed } from "../api/client";
import { certificateDateFormatChoice } from "./certificateDateFormats.ts";

export const releaseRecordIssuePoint = z.enum(["at_first_shipment", "at_build_release", "on_request"]);
export type ReleaseRecordIssuePoint = z.infer<typeof releaseRecordIssuePoint>;
export const releaseRecordIssuePointOverride = z.object({ itemClass: z.string(), issuePoint: releaseRecordIssuePoint });
export type ReleaseRecordIssuePointOverride = z.infer<typeof releaseRecordIssuePointOverride>;
export const releaseRecordIssuePointSettings = z.object({ defaultIssuePoint: releaseRecordIssuePoint,
  itemClassOverrides: z.array(releaseRecordIssuePointOverride), version: z.string() });
export type ReleaseRecordIssuePointSettings = z.infer<typeof releaseRecordIssuePointSettings>;
export const certificateDateFormatSettings = z.object({ languages: z.array(certificateDateFormatChoice),
  version: z.string() });
export type CertificateDateFormatSettings = z.infer<typeof certificateDateFormatSettings>;

export const certificateSettingsApi = {
  get: (signal?: AbortSignal | undefined) => apiParsed(
    "/api/certificates/settings/release-record-issue-point", releaseRecordIssuePointSettings, { signal }),
  save: (settings: ReleaseRecordIssuePointSettings) => apiParsed(
    "/api/certificates/settings/release-record-issue-point", releaseRecordIssuePointSettings, {
      method: "PUT",
      body: {
        defaultIssuePoint: settings.defaultIssuePoint,
        itemClassOverrides: settings.itemClassOverrides,
        expectedVersion: settings.version,
      },
    }),
  getDateFormats: (signal?: AbortSignal | undefined) => apiParsed(
    "/api/certificates/settings/date-format", certificateDateFormatSettings, { signal }),
  saveDateFormats: (settings: CertificateDateFormatSettings) => apiParsed(
    "/api/certificates/settings/date-format", certificateDateFormatSettings, {
      method: "PUT",
      body: { languages: settings.languages, expectedVersion: settings.version },
    }),
};
