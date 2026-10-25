import * as z from "zod/mini";
import { apiParsed, checkedBody } from "../api/client.ts";
import { certificateDateFormatChoice } from "./certificateDateFormats.ts";

export const releaseRecordIssuePoint = z.enum(["at_first_shipment", "at_build_release", "on_request"]);
export type ReleaseRecordIssuePoint = z.infer<typeof releaseRecordIssuePoint>;
export const releaseRecordBomContent = z.enum(["serialised_only", "full_bom"]);
export type ReleaseRecordBomContent = z.infer<typeof releaseRecordBomContent>;
export const releaseRecordIssuePointOverride = z.object({ itemClass: z.string(), issuePoint: releaseRecordIssuePoint,
  bomContent: releaseRecordBomContent });
export type ReleaseRecordIssuePointOverride = z.infer<typeof releaseRecordIssuePointOverride>;
export const releaseRecordIssuePointSettings = z.object({ defaultIssuePoint: releaseRecordIssuePoint,
  defaultBomContent: releaseRecordBomContent, itemClassOverrides: z.array(releaseRecordIssuePointOverride), version: z.string() });
export type ReleaseRecordIssuePointSettings = z.infer<typeof releaseRecordIssuePointSettings>;
const updateReleaseRecordIssuePointSettings = z.object({ defaultIssuePoint: releaseRecordIssuePoint,
  defaultBomContent: releaseRecordBomContent, itemClassOverrides: z.array(releaseRecordIssuePointOverride), expectedVersion: z.string() });
export const certificateDateFormatSettings = z.object({ languages: z.array(certificateDateFormatChoice),
  version: z.string() });
export type CertificateDateFormatSettings = z.infer<typeof certificateDateFormatSettings>;

export const certificateSettingsApi = {
  get: (signal?: AbortSignal | undefined) => apiParsed(
    "/api/certificates/settings/release-record-issue-point", releaseRecordIssuePointSettings, { signal }),
  save: (settings: ReleaseRecordIssuePointSettings) => apiParsed(
    "/api/certificates/settings/release-record-issue-point", releaseRecordIssuePointSettings, {
      method: "PUT",
      body: checkedBody(updateReleaseRecordIssuePointSettings, {
        defaultIssuePoint: settings.defaultIssuePoint,
        defaultBomContent: settings.defaultBomContent,
        itemClassOverrides: settings.itemClassOverrides,
        expectedVersion: settings.version,
      }),
    }),
  getDateFormats: (signal?: AbortSignal | undefined) => apiParsed(
    "/api/certificates/settings/date-format", certificateDateFormatSettings, { signal }),
  saveDateFormats: (settings: CertificateDateFormatSettings) => apiParsed(
    "/api/certificates/settings/date-format", certificateDateFormatSettings, {
      method: "PUT",
      body: checkedBody(z.object({ languages: z.array(certificateDateFormatChoice), expectedVersion: z.string() }),
        { languages: settings.languages, expectedVersion: settings.version }),
    }),
};
