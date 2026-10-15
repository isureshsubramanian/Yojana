import { setMode, useMode, type Mode } from "./mode";

const options: { value: Mode; label: string }[] = [
  { value: "system", label: "Match system" },
  { value: "light", label: "Light" },
  { value: "dark", label: "Dark" },
];

/** Radio-style menu entries for the appearance group of the user menu. */
export function ModeMenuItems() {
  const mode = useMode();
  return (
    <div role="radiogroup" aria-label="Appearance">
      {options.map((o) => (
        <button key={o.value} type="button" role="radio" className="mi" aria-checked={mode === o.value} onClick={() => setMode(o.value)}>
          {o.label}
        </button>
      ))}
    </div>
  );
}
