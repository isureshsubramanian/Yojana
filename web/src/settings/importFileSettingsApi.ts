import * as z from "zod/mini";
import { apiParsed } from "../api/client.ts";

export const importFileArchiveStoreOption = z.object({ name: z.string(), kind: z.enum(["folder", "database"]) });
export type ImportFileArchiveStoreOption = z.infer<typeof importFileArchiveStoreOption>;
export const importFileStorageSettings = z.object({ storage: z.enum(["file", "database"]), archiveStore: z.string(),
  archiveAge: z.number(), archiveAgeUnit: z.enum(["days", "months", "years"]),
  archiveStores: z.array(importFileArchiveStoreOption), version: z.string() });
export type ImportFileStorageSettings = z.infer<typeof importFileStorageSettings>;

export const importFileSettingsApi = {
  get: (signal?: AbortSignal) => apiParsed("/api/settings/import-files", importFileStorageSettings, { signal }),
  save: (settings: ImportFileStorageSettings) => apiParsed("/api/settings/import-files", importFileStorageSettings, {
    method: "PUT",
    body: { storage: settings.storage, archiveStore: settings.archiveStore,
      archiveAge: settings.archiveAge, archiveAgeUnit: settings.archiveAgeUnit, expectedVersion: settings.version },
  }),
};
