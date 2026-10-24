import { api } from "../api/client";
import type { ScanningPolicy } from "../components/scan";

export type { ScanningPolicy } from "../components/scan";

export const scanningApi = {
  get: () => api<ScanningPolicy>("/api/settings/scanning"),
  save: (policy: ScanningPolicy) => api<ScanningPolicy>("/api/settings/scanning", { method: "PUT", body: policy }),
};
