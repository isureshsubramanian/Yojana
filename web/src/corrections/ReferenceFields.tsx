import { shortId } from "../units/format";

// Reference fields: how a correction screen asks for, and shows, an item, a business partner or a document. Screens use
// these instead of raw id inputs so the user works with codes, names and document numbers; the lookups behind them are
// narrow read contracts served by /api/serialisation/reference-names and the document search.
//
// INTERFACE STUB (screen-gap fixes): the signatures below are the agreed contract. The implementation is the plain-id
// fallback until the lookup endpoints land; replace the bodies, never the props, so callers keep working.

export type DocumentValue = { type: string; id: string };

/** Labels for references a screen shows. Pass every reference the screen renders; each label falls back to a short id until the name arrives. */
export function useReferenceNames(_references: { items?: readonly (string | null | undefined)[]; parties?: readonly (string | null | undefined)[]; documents?: readonly (DocumentValue | null | undefined)[] }): {
  item: (id: string | null | undefined) => string;
  party: (id: string | null | undefined) => string;
  document: (doc: DocumentValue | null | undefined) => string;
} {
  return {
    item: (id) => id ? `item ${shortId(id)}` : "—",
    party: (id) => id ? `partner ${shortId(id)}` : "—",
    document: (doc) => doc ? `${doc.type.replace(/_/g, " ")} ${shortId(doc.id)}` : "—",
  };
}

type FieldProps<T> = { id: string; label: string; value: T | null; onChange: (value: T | null) => void; optional?: boolean; hint?: string };

/** An item, chosen by code or name; the value is the item id. */
export function ItemField({ id, label, value, onChange, optional, hint }: FieldProps<string>) {
  return <div className="field"><label htmlFor={id}>{label}{optional && <span className="hint"> · optional</span>}</label><input id={id} className="input serial" value={value ?? ""} onChange={(event) => onChange(event.target.value.trim() || null)} />{hint && <span className="hint">{hint}</span>}</div>;
}

/** A business partner (customer, vendor, carrier), chosen by code or name; the value is the party reference. */
export function PartyField({ id, label, value, onChange, optional, hint }: FieldProps<string>) {
  return <div className="field"><label htmlFor={id}>{label}{optional && <span className="hint"> · optional</span>}</label><input id={id} className="input serial" value={value ?? ""} onChange={(event) => onChange(event.target.value.trim() || null)} />{hint && <span className="hint">{hint}</span>}</div>;
}

/** A document of one of the given types (e.g. ["production_order"]), chosen by its number; the value is the document reference. */
export function DocumentField({ id, label, types, value, onChange, optional, hint }: FieldProps<DocumentValue> & { types: readonly string[] }) {
  const type = value?.type ?? types[0] ?? "";
  return <div className="field"><label htmlFor={id}>{label}{optional && <span className="hint"> · optional</span>}</label><div style={{ display: "flex", gap: 8 }}>{types.length > 1 && <select className="select" aria-label={`${label} type`} value={type} onChange={(event) => onChange(value ? { ...value, type: event.target.value } : null)}>{types.map((t) => <option key={t} value={t}>{t.replace(/_/g, " ")}</option>)}</select>}<input id={id} className="input serial" value={value?.id ?? ""} onChange={(event) => onChange(event.target.value.trim() ? { type, id: event.target.value.trim() } : null)} /></div>{hint && <span className="hint">{hint}</span>}</div>;
}
