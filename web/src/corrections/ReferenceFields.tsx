import { useCallback, useEffect, useId, useRef, useState, useSyncExternalStore, type KeyboardEvent } from "react";
import { apiParsed, checkedBody } from "../api/client.ts";
import { units, type UnitDetail } from "../api/units";
import { useSession } from "../session/session";
import {
  createReferenceStore, documentLabelText, itemLabelText, locationLabelText, lotLabelText, partyLabelText,
  type ReferenceStore,
} from "./referenceStore";
import {
  itemFamilyOptions,
  referenceNamesRequest,
  referenceNamesResponse,
  referenceSearchResponse,
  type DocumentValue,
  type ItemFamilyOption,
  type SearchResult,
} from "./referenceSchemas.ts";

// Reference fields: how a screen asks for, and shows, an item, business partner, location or document. Screens use
// these instead of raw id inputs so the user works with codes, names and document numbers; the values crossing the
// component boundary remain stable ids and DocumentRefs.

export type { DocumentValue, ItemFamilyOption } from "./referenceSchemas.ts";

// Contract example types plus document references recorded by Serialisation. Keeping this boundary narrow makes a
// picker alias fail type-checking instead of silently selecting no units.
export const DOCUMENT_PICKER_TYPES = [
  "adjustment", "delivery", "invoice", "lot", "production_order", "receipt", "return", "sales_order", "stocktake", "transfer",
] as const;
export type DocumentPickerType = (typeof DOCUMENT_PICKER_TYPES)[number];

const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const stores = new Map<string, ReferenceStore>();

function storeFor(tenant: string): ReferenceStore {
  let store = stores.get(tenant);
  if (!store) {
    store = createReferenceStore((body) => apiParsed("/api/serialisation/reference-names", referenceNamesResponse, { method: "POST", body: checkedBody(referenceNamesRequest, body) }));
    stores.set(tenant, store);
  }
  return store;
}

/** Labels for references a screen shows. Pass every reference the screen renders; each label falls back to a short id until the name arrives. */
export function useReferenceNames(references: { items?: readonly (string | null | undefined)[]; parties?: readonly (string | null | undefined)[]; documents?: readonly (DocumentValue | null | undefined)[]; locations?: readonly (string | null | undefined)[] }): {
  item: (id: string | null | undefined) => string;
  itemName: (id: string | null | undefined) => string | null;
  party: (id: string | null | undefined) => string;
  document: (doc: DocumentValue | null | undefined) => string;
  lot: (doc: DocumentValue | null | undefined) => string;
  location: (id: string | null | undefined) => string;
} {
  const session = useSession();
  const store = storeFor(session.tenant?.code ?? "");
  const version = useSyncExternalStore(store.subscribe, store.version, store.version);
  const lotItemIds = (references.documents ?? []).filter((document) => document?.type === "lot")
    .map((document) => document ? store.document(document)?.itemId : null);
  const itemKey = [...(references.items ?? []), ...lotItemIds].filter(Boolean).join("\n");
  const partyKey = (references.parties ?? []).filter(Boolean).join("\n");
  const documentKey = (references.documents ?? []).filter(Boolean).map((document) => `${document!.type}:${document!.id}`).join("\n");
  const locationKey = (references.locations ?? []).filter(Boolean).join("\n");
  useEffect(() => {
    store.request({
      items: itemKey ? itemKey.split("\n") : [],
      parties: partyKey ? partyKey.split("\n") : [],
      documents: documentKey ? documentKey.split("\n").map((value) => { const colon = value.indexOf(":"); return { type: value.slice(0, colon), id: value.slice(colon + 1) }; }) : [],
      locations: locationKey ? locationKey.split("\n") : [],
    });
  }, [store, itemKey, partyKey, documentKey, locationKey]);
  return {
    item: useCallback((id) => itemLabelText(id, id ? store.item(id) : undefined), [store, version]),
    itemName: useCallback((id) => id ? store.item(id)?.name ?? store.item(id)?.code ?? null : null, [store, version]),
    party: useCallback((id) => partyLabelText(id, id ? store.party(id) : undefined), [store, version]),
    document: useCallback((document) => documentLabelText(document, document ? store.document(document) : undefined), [store, version]),
    lot: useCallback((document) => {
      const documentLabel = document ? store.document(document) : undefined;
      return lotLabelText(document, documentLabel, documentLabel?.itemId ? store.item(documentLabel.itemId) : undefined);
    }, [store, version]),
    location: useCallback((id) => locationLabelText(id, id ? store.location(id) : undefined), [store, version]),
  };
}

/** Item-family names and picker options supplied through the item module's narrow name port. */
export function useItemFamilies(): { options: ItemFamilyOption[]; name: (code: string | null | undefined) => string } {
  const [options, setOptions] = useState<ItemFamilyOption[]>([]);
  useEffect(() => {
    const controller = new AbortController();
    apiParsed("/api/serialisation/item-families", itemFamilyOptions, { signal: controller.signal }).then(setOptions)
      .catch((reason) => { if (!aborted(reason)) setOptions([]); });
    return () => controller.abort();
  }, []);
  return {
    options,
    name: useCallback((code) => {
      if (!code) return "—";
      return options.find((option) => option.code.toLocaleLowerCase() === code.toLocaleLowerCase())?.name ?? friendlyType(code);
    }, [options]),
  };
}

/** An item family shown by name while retaining its stable code as the value. */
export function ItemFamilyField({ id, label, value, onChange, optional, hint, disabled, invalid }: FieldProps<string> & { disabled?: boolean | undefined; invalid?: boolean | undefined }) {
  const families = useItemFamilies();
  return <label className="field" htmlFor={id}><span className="lbl">{label}{optional && <span className="hint"> · optional</span>}</span>
    <select id={id} className="select" value={value ?? ""} disabled={disabled} aria-invalid={invalid || undefined} aria-describedby={hint ? `${id}-help` : undefined} onChange={(event) => onChange(event.target.value || null)}>
      <option value="">{optional ? "Any item class" : "Choose an item class…"}</option>
      {value && !families.options.some((option) => option.code === value) && <option value={value}>{families.name(value)}</option>}
      {families.options.map((option) => <option key={option.code} value={option.code} disabled={Boolean(option.availabilityCode)}>{option.name}</option>)}
    </select>{hint && <span className={invalid ? "help bad" : "help"} id={`${id}-help`}>{hint}</span>}
  </label>;
}

type FieldProps<T> = { id: string; label: string; value: T | null; onChange: (value: T | null) => void; optional?: boolean | undefined; hint?: string | undefined };

/** An item, chosen by code or name; the value is the item id. */
export function ItemField({ id, label, value, onChange, optional, hint }: FieldProps<string>) {
  const names = useReferenceNames({ items: [value] });
  return <ReferencePicker id={id} label={label} value={value} selectedLabel={names.item(value)} onChange={onChange} optional={optional} hint={hint}
    kind="item" placeholder="Search by item code or name" noResult={(query) => `No item ${query} found`} />;
}

/** A business partner (customer, vendor, carrier), chosen by code or name; the value is the party reference. */
export function PartyField({ id, label, value, onChange, optional, hint }: FieldProps<string>) {
  const names = useReferenceNames({ parties: [value] });
  return <ReferencePicker id={id} label={label} value={value} selectedLabel={names.party(value)} onChange={onChange} optional={optional} hint={hint}
    kind="party" placeholder="Search by partner code or name" noResult={(query) => `No business partner ${query} found`} />;
}

const CUSTOMER = ["customer"] as const;

/** A customer party, chosen by code or name; vendors, carriers and the tenant's own legal entities stay out. */
export function CustomerField({ id, label, value, onChange, optional, hint }: FieldProps<string>) {
  const names = useReferenceNames({ parties: [value] });
  return <ReferencePicker id={id} label={label} value={value} selectedLabel={names.party(value)} onChange={onChange} optional={optional} hint={hint}
    kind="party" partyTypes={CUSTOMER} placeholder="Search by customer code or name" noResult={(query) => `No customer ${query} found`} />;
}

const LEGAL_ENTITY = ["legal_entity"] as const;

/** A legal entity of the company (the one that owes a warranty, issues a document), chosen by code or name; the value is its party reference. */
export function LegalEntityField({ id, label, value, onChange, optional, hint }: FieldProps<string>) {
  const names = useReferenceNames({ parties: [value] });
  return <ReferencePicker id={id} label={label} value={value} selectedLabel={names.party(value)} onChange={onChange} optional={optional} hint={hint}
    kind="party" partyTypes={LEGAL_ENTITY} placeholder="Search by company code or name" noResult={(query) => `No legal entity ${query} found`} />;
}

/** A stock or service location, chosen by code or name; the value is the location id. */
export function LocationField({ id, label, value, onChange, optional, hint, kind = "quarantine" }: FieldProps<string> & { kind?: string | undefined }) {
  const names = useReferenceNames({ locations: [value] });
  return <ReferencePicker id={id} label={label} value={value} selectedLabel={names.location(value)} onChange={onChange} optional={optional} hint={hint}
    kind="location" locationKind={kind} acceptGuid={false} placeholder="Search by location code or name" noResult={(query) => `No ${friendlyType(kind)} location ${query} found`} />;
}

/** A document of one of the given types (e.g. ["production_order"]), chosen by its number; the value is the document reference. */
export function DocumentField({ id, label, types, value, onChange, optional, hint }: FieldProps<DocumentValue> & { types: readonly DocumentPickerType[] }) {
  const [selectedType, setSelectedType] = useState(value?.type ?? types[0] ?? "");
  useEffect(() => { if (value?.type) setSelectedType(value.type); }, [value?.type]);
  const names = useReferenceNames({ documents: [value] });
  const picker = <ReferencePicker id={id} label={label} value={value?.id ?? null} selectedLabel={selectedType === "lot" ? names.lot(value) : names.document(value)}
    onChange={(documentId) => onChange(documentId ? { type: selectedType, id: documentId } : null)} optional={optional} hint={hint}
    kind="document" documentType={selectedType} placeholder={`Search by ${friendlyType(selectedType)} number`}
    noResult={(query) => `No ${friendlyType(selectedType)} ${query} found`} hideLabel={types.length > 1} />;
  if (types.length <= 1) return picker;
  return <div className="reference-field-with-type"><div className="field"><label htmlFor={`${id}-type`}>{label}{optional && <span className="hint"> · optional</span>}</label><select id={`${id}-type`} className="select" value={selectedType} onChange={(event) => { setSelectedType(event.target.value); if (value) onChange(null); }}>{types.map((candidate) => <option key={candidate} value={candidate}>{sentence(friendlyType(candidate))}</option>)}</select></div>{picker}</div>;
}

/** A serialised unit, chosen by any live identifier; the value is the unit id. Shown as the serial a person recognises and its item. */
export function UnitField({ id, label, value, onChange, optional, hint }: FieldProps<string>) {
  const [serial, setSerial] = useState<{ unitId: string; serial: string; itemId: string } | null>(null);
  useEffect(() => {
    if (!value || serial?.unitId === value) return;
    const controller = new AbortController();
    units.detail(value, controller.signal).then((unit) => setSerial({ unitId: value, serial: displaySerial(unit) ?? value.slice(0, 8), itemId: unit.itemId }))
      .catch(() => { if (!controller.signal.aborted) setSerial({ unitId: value, serial: value.slice(0, 8), itemId: "" }); });
    return () => controller.abort();
  }, [value, serial?.unitId]);
  const names = useReferenceNames({ items: [serial?.unitId === value ? serial.itemId || null : null] });
  const known = value && serial?.unitId === value ? serial : null;
  const selectedLabel = !value ? "" : known ? (known.itemId ? `${known.serial} · ${names.item(known.itemId)}` : known.serial) : "…";
  return <ReferencePicker id={id} label={label} value={value} selectedLabel={selectedLabel} onChange={onChange} optional={optional} hint={hint}
    kind="unit" placeholder="Search by serial or other identifier" noResult={(query) => `No unit ${query} found`} />;
}

/** The serial a person recognises: the live internal serial as written, else any live identifier (mirrors Claims/UnitSerials.cs). */
function displaySerial(unit: UnitDetail): string | null {
  const live = unit.identifiers.filter((identifier) => identifier.validTo === null);
  return (live.find((identifier) => identifier.type === "internal_serial") ?? [...live].sort((a, b) => a.type.localeCompare(b.type))[0])?.rawValue ?? null;
}

/** A lot document, chosen by its number; the value crossing the component boundary is the lot id. */
export function LotField({ id, label, value, onChange, optional, hint }: FieldProps<string>) {
  return <DocumentField id={id} label={label} types={["lot"]} value={value ? { type: "lot", id: value } : null}
    onChange={(document) => onChange(document?.id ?? null)} optional={optional} hint={hint} />;
}

function ReferencePicker({ id, label, value, selectedLabel, onChange, optional, hint, kind, documentType, locationKind, partyTypes, acceptGuid = true, placeholder, noResult, hideLabel = false }: {
  id: string; label: string; value: string | null; selectedLabel: string; onChange: (value: string | null) => void; optional: boolean | undefined; hint: string | undefined;
  kind: "item" | "party" | "location" | "document" | "unit"; documentType?: string; locationKind?: string; partyTypes?: readonly string[]; acceptGuid?: boolean;
  placeholder: string; noResult: (query: string) => string; hideLabel?: boolean;
}) {
  const listId = `${useId().replace(/:/g, "")}-reference-options`;
  const [query, setQuery] = useState("");
  const [results, setResults] = useState<SearchResult[]>([]);
  const [active, setActive] = useState(-1);
  const [open, setOpen] = useState(false);
  const [loading, setLoading] = useState(false);
  const [message, setMessage] = useState<string | null>(null);
  const request = useRef<AbortController | null>(null);
  const resultNames = useReferenceNames({ items: results.map((result) => result.itemId) });

  const search = useCallback(async (text: string, signal: AbortSignal) => {
    // A party-type filter reads the widest page and keeps the matching types, so a short list is not crowded out.
    const params = new URLSearchParams({ kind, q: text, take: partyTypes ? "25" : "10" });
    if (documentType) params.set("type", documentType);
    if (locationKind) params.set("locationKind", locationKind);
    const results = (await apiParsed(`/api/serialisation/reference-search?${params}`, referenceSearchResponse, { signal })).results;
    return partyTypes ? results.filter((result) => result.partyType !== null && partyTypes.includes(result.partyType)) : results;
  }, [kind, documentType, locationKind, partyTypes]);

  useEffect(() => {
    const text = query.trim();
    if (!text || value) { setResults([]); setOpen(false); setLoading(false); return; }
    const controller = new AbortController();
    request.current?.abort(); request.current = controller;
    const timer = window.setTimeout(() => {
      setLoading(true);
      search(text, controller.signal).then((next) => { setResults(next); setActive(next.length ? 0 : -1); setOpen(true); setLoading(false); })
        .catch((reason) => { if (!aborted(reason)) { setResults([]); setOpen(true); setLoading(false); } });
    }, 200);
    return () => { window.clearTimeout(timer); controller.abort(); };
  }, [query, value, search]);

  const choose = (result: SearchResult) => {
    onChange(result.id); setQuery(""); setResults([]); setOpen(false); setMessage(null);
  };
  const resolveEntry = async () => {
    const text = query.trim();
    if (!text) { if (optional) onChange(null); return; }
    if (acceptGuid && GUID.test(text)) { onChange(text); setQuery(""); setOpen(false); setMessage(null); return; }
    let choices = results;
    if (!choices.length) {
      request.current?.abort();
      const controller = new AbortController(); request.current = controller; setLoading(true);
      try { choices = await search(text, controller.signal); setResults(choices); } catch (reason) { if (aborted(reason)) return; choices = []; } finally { setLoading(false); }
    }
    const exact = choices.find((result) => (kind === "document" || kind === "unit" ? result.number : result.code)?.toLowerCase() === text.toLowerCase());
    const choice = exact ?? (active >= 0 ? choices[active] : undefined);
    if (choice) choose(choice);
    else { setMessage(noResult(text)); setOpen(true); }
  };
  const keyDown = (event: KeyboardEvent<HTMLInputElement>) => {
    if (event.key === "ArrowDown") { event.preventDefault(); setOpen(true); setActive((current) => results.length ? (current + 1 + results.length) % results.length : -1); }
    else if (event.key === "ArrowUp") { event.preventDefault(); setOpen(true); setActive((current) => results.length ? (current - 1 + results.length) % results.length : -1); }
    else if (event.key === "Enter") { event.preventDefault(); if (open && active >= 0 && results[active]) choose(results[active]); else void resolveEntry(); }
    else if (event.key === "Escape") { event.preventDefault(); setOpen(false); setMessage(null); }
  };

  return <div className="field reference-field">
    {hideLabel ? <span className="lbl" aria-hidden="true">&nbsp;</span> : <label htmlFor={id}>{label}{optional && <span className="hint"> · optional</span>}</label>}
    {value ? <div className="reference-selected"><span title={value}>{selectedLabel}</span><button type="button" className="reference-clear" aria-label={`Clear ${label}`} onClick={() => onChange(null)}>Clear</button></div>
      : <div className="reference-combobox">
        <input id={id} className="input" role="combobox" aria-autocomplete="list" aria-expanded={open} aria-controls={listId}
          aria-label={hideLabel ? `${label} number` : undefined}
          aria-activedescendant={open && active >= 0 ? `${listId}-${active}` : undefined} aria-invalid={message ? true : undefined}
          value={query} placeholder={placeholder} onChange={(event) => { setQuery(event.target.value); setResults([]); setActive(-1); setMessage(null); setOpen(true); }}
          onFocus={() => { if (query.trim()) setOpen(true); }} onBlur={() => window.setTimeout(() => setOpen(false), 100)} onKeyDown={keyDown} />
        {loading && <span className="reference-loading" aria-hidden="true">…</span>}
        {open && results.length > 0 && <div id={listId} className="reference-options" role="listbox">
          {results.map((result, index) => <button type="button" role="option" id={`${listId}-${index}`} aria-selected={active === index}
            className="reference-option" key={`${result.type ?? kind}:${result.id}`} onMouseDown={(event) => event.preventDefault()} onClick={() => choose(result)}>
            <span>{resultLabel(result, resultNames.itemName(result.itemId))}</span>{resultMeta(result) && <span>{resultMeta(result)}</span>}
          </button>)}
        </div>}
      </div>}
    {message && <span className="reference-error" role="alert">{message}</span>}
    {hint && <span className="hint">{hint}</span>}
  </div>;
}

function resultLabel(result: SearchResult, itemName: string | null): string {
  if (result.kind === "item") return result.name ? `${result.code} · ${result.name}` : result.code ?? result.id;
  if (result.kind === "party") return result.name ? `${result.name} (${result.code})` : result.code ?? result.id;
  if (result.kind === "location") return result.name ? `${result.code} · ${result.name}` : result.code ?? result.id;
  const number = result.number ?? result.id;
  return (result.type === "lot" || result.kind === "unit") && itemName ? `${number} · ${itemName}` : number;
}

function resultMeta(result: SearchResult): string | null {
  if (result.kind === "party") return result.partyType ? friendlyType(result.partyType) : null;
  if (result.kind === "location") return result.type ? friendlyType(result.type) : null;
  if (result.kind === "document" || result.kind === "unit") return result.status ? friendlyType(result.status) : null;
  return null;
}

function friendlyType(value: string): string { return value.replace(/_/g, " "); }
function sentence(value: string): string { return value.charAt(0).toUpperCase() + value.slice(1); }
function aborted(reason: unknown): boolean { return reason instanceof DOMException && reason.name === "AbortError"; }
