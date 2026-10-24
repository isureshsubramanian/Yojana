import { useEffect, useState } from "react";
import { api } from "./client";

export interface LegalEntityOption { id: string; code: string; name: string }
export interface LegalEntityOptions { defaultId: string | null; entities: LegalEntityOption[] }

export const legalEntitiesApi = {
  get: (signal?: AbortSignal) => api<LegalEntityOptions>("/api/settings/legal-entities", signal ? { signal } : undefined),
  setDefault: (partyId: string) => api<LegalEntityOptions>("/api/settings/legal-entities/default", { method: "PUT", body: { partyId } }),
};

export function useLegalEntities(enabled = true) {
  const [value, setValue] = useState<LegalEntityOptions | null>(null);
  const [error, setError] = useState<unknown>(null);
  useEffect(() => {
    if (!enabled) return;
    const controller = new AbortController();
    void legalEntitiesApi.get(controller.signal).then(setValue).catch((reason) => {
      if (!(reason instanceof DOMException && reason.name === "AbortError")) setError(reason);
    });
    return () => controller.abort();
  }, [enabled]);
  return { value, error, setValue };
}

export function LegalEntityPicker({ id, label, value, entities, onChange, hint }: {
  id: string; label: string; value: string; entities: LegalEntityOption[];
  onChange: (value: string) => void; hint?: string;
}) {
  return <div className="field"><label htmlFor={id}>{label}</label><select id={id} className="select" value={value} onChange={(event) => onChange(event.target.value)}>
    <option value="">Choose a legal entity</option>
    {entities.map((entity) => <option value={entity.id} key={entity.id}>{entity.name} · {entity.code}</option>)}
  </select>{hint && <span className="help">{hint}</span>}</div>;
}
