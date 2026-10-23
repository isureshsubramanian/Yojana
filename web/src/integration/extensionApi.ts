import { api } from "../api/client";
import type { ConnectionExtensionView } from "./extensionTypes";

const root = (connectionId: string) => `/api/integration/connections/${connectionId}/extension`;

export const extensionApi = {
  read: (connectionId: string, signal?: AbortSignal) =>
    api<ConnectionExtensionView>(root(connectionId), { signal }),
  check: (connectionId: string) =>
    api<ConnectionExtensionView>(`${root(connectionId)}/check`, { method: "POST" }),
};
