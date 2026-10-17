export interface ItemReferenceLabel { code: string; name: string | null }
export interface PartyReferenceLabel { code: string; name: string | null; partyType: string | null }
export interface DocumentReferenceLabel { number: string; status: string | null; itemId: string | null }
export interface ReferenceDocument { type: string; id: string }

export interface ReferenceBatch {
  items: Record<string, ItemReferenceLabel>;
  parties: Record<string, PartyReferenceLabel>;
  documents: Record<string, DocumentReferenceLabel>;
}

export interface ReferenceRequest {
  items: string[];
  parties: string[];
  documents: ReferenceDocument[];
}

export type ResolveReferences = (request: ReferenceRequest) => Promise<ReferenceBatch>;

export interface ReferenceStore {
  request(references: { items?: readonly (string | null | undefined)[]; parties?: readonly (string | null | undefined)[]; documents?: readonly (ReferenceDocument | null | undefined)[] }): void;
  item(id: string): ItemReferenceLabel | undefined;
  party(id: string): PartyReferenceLabel | undefined;
  document(document: ReferenceDocument): DocumentReferenceLabel | undefined;
  subscribe(listener: () => void): () => void;
  version(): number;
}

/** Mirrors the server endpoint's per-kind cap; change both sides together so a client batch is never refused. */
export const MAX_REFERENCE_NAMES = 200;

const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

export function referenceDocumentKey(document: ReferenceDocument): string {
  return `${document.type.toLowerCase()}:${document.id.toLowerCase()}`;
}

export function itemLabelText(id: string | null | undefined, label: ItemReferenceLabel | null | undefined): string {
  if (!id) return "—";
  return label ? label.name ? `${label.code} · ${label.name}` : label.code : shortReference(id);
}

export function partyLabelText(id: string | null | undefined, label: PartyReferenceLabel | null | undefined): string {
  if (!id) return "—";
  return label ? label.name ? `${label.name} (${label.code})` : label.code : shortReference(id);
}

export function documentLabelText(document: ReferenceDocument | null | undefined, label: DocumentReferenceLabel | null | undefined): string {
  if (!document) return "—";
  return label?.number ?? shortReference(document.id);
}

export function lotLabelText(document: ReferenceDocument | null | undefined, documentLabel: DocumentReferenceLabel | null | undefined, itemLabel: ItemReferenceLabel | null | undefined): string {
  const number = documentLabelText(document, documentLabel);
  const itemName = itemLabel?.name ?? itemLabel?.code;
  return itemName ? `${number} · ${itemName}` : number;
}

export function createReferenceStore(resolve: ResolveReferences): ReferenceStore {
  const items = new Map<string, ItemReferenceLabel | null>();
  const parties = new Map<string, PartyReferenceLabel | null>();
  const documents = new Map<string, DocumentReferenceLabel | null>();
  const queuedItems = new Set<string>();
  const queuedParties = new Set<string>();
  const queuedDocuments = new Map<string, ReferenceDocument>();
  const listeners = new Set<() => void>();
  let currentVersion = 0;
  let scheduled = false;

  const flush = async () => {
    scheduled = false;
    const itemBatch = [...queuedItems]; queuedItems.clear();
    const partyBatch = [...queuedParties]; queuedParties.clear();
    const documentBatch = [...queuedDocuments.values()]; queuedDocuments.clear();
    const calls = Math.ceil(Math.max(itemBatch.length, partyBatch.length, documentBatch.length) / MAX_REFERENCE_NAMES);
    for (let index = 0; index < calls; index += 1) {
      const start = index * MAX_REFERENCE_NAMES;
      const requested = {
        items: itemBatch.slice(start, start + MAX_REFERENCE_NAMES),
        parties: partyBatch.slice(start, start + MAX_REFERENCE_NAMES),
        documents: documentBatch.slice(start, start + MAX_REFERENCE_NAMES),
      };
      let answer: ReferenceBatch = { items: {}, parties: {}, documents: {} };
      try { answer = await resolve(requested); } catch { /* labels are decoration; unresolved references keep their fallback */ }
      const returnedItems = lowerKeys(answer.items);
      const returnedParties = lowerKeys(answer.parties);
      const returnedDocuments = lowerKeys(answer.documents);
      for (const id of requested.items) items.set(id.toLowerCase(), returnedItems[id.toLowerCase()] ?? null);
      for (const id of requested.parties) parties.set(id.toLowerCase(), returnedParties[id.toLowerCase()] ?? null);
      for (const document of requested.documents) {
        const key = referenceDocumentKey(document);
        documents.set(key, returnedDocuments[key] ?? null);
      }
      currentVersion += 1;
      listeners.forEach((listener) => listener());
    }
  };

  return {
    request(references) {
      for (const id of references.items ?? []) if (id && GUID.test(id) && !items.has(id.toLowerCase())) queuedItems.add(id);
      for (const id of references.parties ?? []) if (id && GUID.test(id) && !parties.has(id.toLowerCase())) queuedParties.add(id);
      for (const document of references.documents ?? []) {
        if (!document || !GUID.test(document.id)) continue;
        const key = referenceDocumentKey(document);
        if (!documents.has(key)) queuedDocuments.set(key, document);
      }
      if ((queuedItems.size || queuedParties.size || queuedDocuments.size) && !scheduled) {
        scheduled = true;
        queueMicrotask(() => { void flush(); });
      }
    },
    item: (id) => items.get(id.toLowerCase()) ?? undefined,
    party: (id) => parties.get(id.toLowerCase()) ?? undefined,
    document: (document) => documents.get(referenceDocumentKey(document)) ?? undefined,
    subscribe(listener) { listeners.add(listener); return () => { listeners.delete(listener); }; },
    version: () => currentVersion,
  };
}

function shortReference(id: string): string {
  return id.replace(/-/g, "").slice(0, 8).toUpperCase();
}

function lowerKeys<T>(values: Record<string, T>): Record<string, T> {
  return Object.fromEntries(Object.entries(values).map(([key, value]) => [key.toLowerCase(), value]));
}
