import { useId, useState, type KeyboardEvent } from "react";
import { countryLabel, filterCountryOptions } from "./countries";
import "./country-select.css";

export function CountrySelect({ id, label, value, onChange, disabled = false, hint, hideLabel = false }: {
  id: string;
  label: string;
  value: string;
  onChange: (code: string) => void;
  disabled?: boolean;
  hint?: string;
  /** Keeps the label for screen readers only, for a picker that sits under a group heading. */
  hideLabel?: boolean;
}) {
  const listId = `${useId().replace(/:/g, "")}-countries`;
  const [query, setQuery] = useState("");
  const [open, setOpen] = useState(false);
  const [active, setActive] = useState(0);
  const results = filterCountryOptions(open ? query : value);

  const choose = (code: string) => {
    onChange(code); setQuery(""); setOpen(false); setActive(0);
  };
  const keyDown = (event: KeyboardEvent<HTMLInputElement>) => {
    if (event.key === "ArrowDown") {
      event.preventDefault(); setOpen(true); setActive((current) => results.length ? (current + 1) % results.length : 0);
    } else if (event.key === "ArrowUp") {
      event.preventDefault(); setOpen(true); setActive((current) => results.length ? (current - 1 + results.length) % results.length : 0);
    } else if (event.key === "Enter" && open && results[active]) {
      event.preventDefault(); choose(results[active].code);
    } else if (event.key === "Escape") {
      event.preventDefault(); setQuery(""); setOpen(false); setActive(0);
    } else if (event.key === "Tab") {
      setOpen(false);
    }
  };

  return <div className="field country-select">
    <label htmlFor={id} className={hideLabel ? "sr-only" : undefined}>{label}</label>
    <div className="country-select-combobox">
      <input id={id} className="input" role="combobox" autoComplete="off" aria-autocomplete="list"
        aria-expanded={open} aria-controls={listId}
        aria-activedescendant={open && results[active] ? `${listId}-${results[active].code}` : undefined}
        disabled={disabled} value={open ? query : countryLabel(value)} placeholder="Search countries"
        onFocus={(event) => { const input = event.currentTarget; const current = countryLabel(value); setQuery(current); setOpen(true); setActive(0); window.setTimeout(() => input.select(), 0); }}
        onChange={(event) => { setQuery(event.target.value); setActive(0); setOpen(true); if (value) onChange(""); }}
        onBlur={() => window.setTimeout(() => { setQuery(""); setOpen(false); }, 100)} onKeyDown={keyDown} />
      {open && <div id={listId} className="country-select-options" role="listbox" aria-label={`${label} options`}>
        {results.map((option, index) => <button type="button" role="option" id={`${listId}-${option.code}`}
          aria-selected={active === index} className="country-select-option" key={option.code}
          onMouseDown={(event) => event.preventDefault()} onMouseEnter={() => setActive(index)} onClick={() => choose(option.code)}>
          <span>{option.name}</span><span>{` (${option.code})`}</span>
        </button>)}
        {results.length === 0 && <span className="country-select-empty" role="status">No countries match.</span>}
      </div>}
    </div>
    {hint && <span className="help">{hint}</span>}
  </div>;
}
