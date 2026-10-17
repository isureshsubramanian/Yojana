// Display names for actor references (a user id stamped by the server, or the e-mail a client sent as actorRef).
// Records keep the reference; screens ask for names here. References asked for in the same tick go to the server in one
// request, every answer is cached for the session, and a reference that does not resolve shows as given (an id
// shortened), so a screen never waits on, or breaks over, a name.

export type ResolveNames = (references: string[]) => Promise<Record<string, string>>;

/**
 * Mirrors IUserDirectory.MaxReferences on the server (Platform.Contracts/Security/IUserDirectory.cs), which refuses
 * larger requests: batches are split here at that size. Change both together.
 */
export const MAX_REFERENCES = 500;

const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/** What to show for a reference: its name, or the reference itself (an id cut to its first block) when there is none. */
export function personLabel(reference: string | null | undefined, name: string | null | undefined): string {
  if (!reference) return "—";
  if (name) return name;
  return GUID.test(reference) ? `${reference.slice(0, 8)}…` : reference;
}

export interface NameStore {
  /** Queues the references not yet known; they are fetched together on the next microtask. */
  request(references: readonly (string | null | undefined)[]): void;
  /** The cached name, or undefined while unknown or unresolvable. */
  name(reference: string): string | undefined;
  subscribe(listener: () => void): () => void;
  /** Changes whenever new names arrive (the snapshot for useSyncExternalStore). */
  version(): number;
}

export function createNameStore(resolve: ResolveNames): NameStore {
  const known = new Map<string, string | null>();
  const queued = new Set<string>();
  const listeners = new Set<() => void>();
  let version = 0;
  let scheduled = false;

  const flush = async () => {
    scheduled = false;
    const batch = [...queued];
    queued.clear();
    for (let start = 0; start < batch.length; start += MAX_REFERENCES) {
      const chunk = batch.slice(start, start + MAX_REFERENCES);
      let names: Record<string, string> = {};
      try { names = await resolve(chunk); } catch { /* names are decoration: show the references as given */ }
      for (const reference of chunk) known.set(reference, names[reference] ?? null);
      version += 1;
      listeners.forEach((listener) => listener());
    }
  };

  return {
    request(references) {
      for (const reference of references) {
        if (reference && !known.has(reference) && !queued.has(reference)) queued.add(reference);
      }
      if (queued.size > 0 && !scheduled) {
        scheduled = true;
        queueMicrotask(() => { void flush(); });
      }
    },
    name: (reference) => known.get(reference) ?? undefined,
    subscribe(listener) { listeners.add(listener); return () => { listeners.delete(listener); }; },
    version: () => version,
  };
}
