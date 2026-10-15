import { useRef, useState, type ChangeEvent, type KeyboardEvent } from "react";
import { Icon } from "../shell/Icons";
import { ScanTimer, separatorForControlKey, submission, type ScanSubmission } from "./scan";

export interface ScanInputProps {
  id?: string;
  value?: string;
  onChange?: (value: string) => void;
  /** Enter, from a scanner or a person. The handler decides what to do with it (lookup, search, add to a list). */
  onSubmit: (scan: ScanSubmission) => void;
  placeholder?: string;
  ariaLabel: string;
  /** Hint shown at the right edge: a key cap or a badge. */
  hint?: React.ReactNode;
  size?: "md" | "lg";
  autoFocus?: boolean;
  /** Marks this field as the page's scan target for the "/" shortcut. */
  primary?: boolean;
  disabled?: boolean;
  describedBy?: string;
}

/**
 * Scan-first input: every serial field accepts a scan. Recovers the FNC1 group separator from the
 * keyboard wedge's control key, classifies Enter as a scan or a typed entry by keystroke timing, and
 * leaves all parsing to the server.
 */
export function ScanInput({ id, value, onChange, onSubmit, placeholder, ariaLabel, hint, size = "md", autoFocus, primary, disabled, describedBy }: ScanInputProps) {
  const [inner, setInner] = useState("");
  const timer = useRef(new ScanTimer());
  const controlled = value !== undefined;
  const current = controlled ? value : inner;
  const set = (next: string) => { if (!controlled) setInner(next); onChange?.(next); };

  const onKeyDown = (e: KeyboardEvent<HTMLInputElement>) => {
    const separator = separatorForControlKey(e.key, e.ctrlKey);
    if (separator) {
      e.preventDefault();
      const input = e.currentTarget;
      const start = input.selectionStart ?? current.length;
      const end = input.selectionEnd ?? start;
      set(current.slice(0, start) + separator + current.slice(end));
      timer.current.key(performance.now());
      return;
    }
    if (e.key === "Enter") {
      e.preventDefault();
      if (!current.trim()) return;
      const source = timer.current.classify(current.length);
      onSubmit(submission(current, source));
      return;
    }
    if (e.key.length === 1) timer.current.key(performance.now());
  };

  const onInput = (e: ChangeEvent<HTMLInputElement>) => set(e.target.value);

  return (
    <div className={`scanbox${size === "lg" ? " lg" : ""}`}>
      <Icon name="scan" />
      <input
        id={id}
        className="input"
        type="text"
        inputMode="text"
        autoComplete="off"
        autoCapitalize="off"
        spellCheck={false}
        aria-label={ariaLabel}
        aria-describedby={describedBy}
        placeholder={placeholder}
        value={current}
        onChange={onInput}
        onKeyDown={onKeyDown}
        autoFocus={autoFocus}
        disabled={disabled}
        data-scan-focus={primary ? "" : undefined}
      />
      {hint && <span className="scan-hint">{hint}</span>}
    </div>
  );
}
