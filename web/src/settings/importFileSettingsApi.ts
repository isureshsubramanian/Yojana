import { api } from "../api/client";

export interface ImportFileArchiveStoreOption { name: string; kind: "folder" | "database" }
export interface ImportFileStorageSettings {
  storage: "file" | "database";
  archiveStore: string;
  archiveAge: number;
  archiveAgeUnit: "days" | "months" | "years";
  archiveStores: ImportFileArchiveStoreOption[];
  version: string;
}

export const importFileSettingsApi = {
  get: (signal?: AbortSignal) => api<ImportFileStorageSettings>("/api/settings/import-files", { signal }),
  save: (settings: ImportFileStorageSettings) => api<ImportFileStorageSettings>("/api/settings/import-files", {
    method: "PUT",
    body: JSON.stringify({ storage: settings.storage, archiveStore: settings.archiveStore,
      archiveAge: settings.archiveAge, archiveAgeUnit: settings.archiveAgeUnit, expectedVersion: settings.version }),
  }),
};
