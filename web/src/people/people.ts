import { useCallback, useEffect, useSyncExternalStore } from "react";
import { api } from "../api/client";
import { useSession } from "../session/session";
import { createNameStore, personLabel, type NameStore } from "./peopleStore";

// One store per tenant: the server resolves only members of the selected tenant, so answers are not shared across them.
const stores = new Map<string, NameStore>();

function storeFor(tenant: string): NameStore {
  let store = stores.get(tenant);
  if (!store) {
    store = createNameStore(async (references) => (await api<{ names: Record<string, string> }>("/api/people/names", { method: "POST", body: { references } })).names);
    stores.set(tenant, store);
  }
  return store;
}

/**
 * Names for the actor references a screen shows. Pass every reference the screen renders; use the returned function
 * where the text goes, e.g. `person(row.requestedBy)`. It shows the reference until the name arrives.
 */
export function usePeople(references: readonly (string | null | undefined)[]): (reference: string | null | undefined) => string {
  const session = useSession();
  const store = storeFor(session.tenant?.code ?? "");
  const version = useSyncExternalStore(store.subscribe, store.version, store.version);
  const key = references.filter(Boolean).join("\n");
  useEffect(() => { store.request(key ? key.split("\n") : []); }, [store, key]);
  // Stable until new names arrive, so screens can list it as a memo dependency.
  return useCallback((reference: string | null | undefined) => personLabel(reference, reference ? store.name(reference) : undefined), [store, version]);
}
