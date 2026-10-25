import { useEffect, useState } from "react";
import * as z from "zod/mini";
import { apiParsed } from "./client.ts";

const legalEntityOption = z.object({ id: z.string(), code: z.string(), name: z.string() });
const legalEntityOptions = z.object({ defaultId: z.nullable(z.string()), entities: z.array(legalEntityOption) });
export type LegalEntityOption = z.infer<typeof legalEntityOption>;
export type LegalEntityOptions = z.infer<typeof legalEntityOptions>;

export const legalEntitiesApi = {
  get: (signal?: AbortSignal) => apiParsed("/api/settings/legal-entities", legalEntityOptions, signal ? { signal } : undefined),
  setDefault: (partyId: string) => apiParsed("/api/settings/legal-entities/default", legalEntityOptions, { method: "PUT", body: { partyId } }),
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
