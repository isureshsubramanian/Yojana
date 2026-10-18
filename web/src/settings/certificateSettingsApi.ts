import { api } from "../api/client";

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
};
