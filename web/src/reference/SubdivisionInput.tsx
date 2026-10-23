import { useEffect, useState } from "react";
import { CountrySelect } from "./CountrySelect";
import { isSubdivisionCode, subdivisionParts } from "./subdivisions";

export function SubdivisionInput({ id, label, value, onChange }: {
  id: string;
  label: string;
  value: string | null;
  onChange: (code: string) => void;
}) {
  const initial = subdivisionParts(value);
  const [country, setCountry] = useState(initial.country);
  const [suffix, setSuffix] = useState(initial.suffix);
  useEffect(() => { const next = subdivisionParts(value); setCountry(next.country); setSuffix(next.suffix); }, [value]);
  const code = country ? `${country}-${suffix}` : "";
  const partial = Boolean(country || suffix);
  const valid = isSubdivisionCode(code);
  const hintId = `${id}-hint`;

  return <fieldset className="subdivision-input">
    <legend>{label}</legend>
    <div className="subdivision-input-fields">
      <CountrySelect id={`${id}-country`} label={`${label} country`} hideLabel value={country} onChange={(next) => { setCountry(next); onChange(next ? `${next}-${suffix}` : ""); }} />
      <div className="field"><label htmlFor={`${id}-suffix`} className="sr-only">{`${label} code suffix`}</label><input id={`${id}-suffix`} className="input mono" placeholder="Code"
        maxLength={3} value={suffix} aria-describedby={hintId} aria-invalid={partial && !valid}
        onChange={(event) => { const next = event.target.value.toUpperCase(); setSuffix(next); onChange(country ? `${country}-${next}` : ""); }} /></div>
    </div>
    <span id={hintId} className={partial && !valid ? "form-error" : "help"} aria-live="polite">
      {partial && !valid ? "Choose a country and enter a 1–3 character suffix using letters or digits." : `ISO 3166-2 code${valid ? `: ${code}` : ", e.g. IN-KA"}`}
    </span>
  </fieldset>;
}
