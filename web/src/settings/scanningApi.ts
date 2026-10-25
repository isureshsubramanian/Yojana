import * as z from "zod/mini";
import { apiParsed } from "../api/client";
import type { ScanningPolicy } from "../components/scan";

export type { ScanningPolicy } from "../components/scan";

// The scanner reads its policy through this client; the annotation keeps the schema and the scanner's type in step.
const scanningPolicy: z.ZodMiniType<ScanningPolicy> = z.object({ endPauseMs: z.number(), repeatWindowMs: z.number() });

export const scanningApi = {
  get: () => apiParsed("/api/settings/scanning", scanningPolicy),
  save: (policy: ScanningPolicy) => apiParsed("/api/settings/scanning", scanningPolicy, { method: "PUT", body: policy }),
};
