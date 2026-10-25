import { apiParsed } from "../api/client.ts";
import { connectionExtensionView } from "./extensionTypes.ts";

const root = (connectionId: string) => `/api/integration/connections/${connectionId}/extension`;

export const extensionApi = {
  read: (connectionId: string, signal?: AbortSignal) =>
    apiParsed(root(connectionId), connectionExtensionView, { signal }),
  check: (connectionId: string) =>
    apiParsed(`${root(connectionId)}/check`, connectionExtensionView, { method: "POST" }),
};
