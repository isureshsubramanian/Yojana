import { useEffect, useRef, useState, type ChangeEvent, type KeyboardEvent } from "react";
import { Icon } from "../shell/Icons";
import { PauseEndedScan, ScanRepeatSuppressor, ScanTimer, separatorForControlKey, type ScanSubmission } from "./scan";
import { useScanSettings } from "./useScanSettings";

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
  const [repeatNote, setRepeatNote] = useState("");
  const settings = useScanSettings();
  const timer = useRef(new ScanTimer());
  const pause = useRef(new PauseEndedScan(timer.current, {
    set: (callback, delayMs) => window.setTimeout(callback, delayMs),
    clear: handle => window.clearTimeout(handle as number),
  }));
  const repeats = useRef(new ScanRepeatSuppressor());
  const noteTimer = useRef<number | null>(null);
  const controlled = value !== undefined;
  const current = controlled ? value : inner;
  const currentRef = useRef(current);
  currentRef.current = current;
  const set = (next: string) => { currentRef.current = next; if (!controlled) setInner(next); onChange?.(next); };

  const emit = (scan: ScanSubmission) => {
    if (!repeats.current.accept(scan.value, performance.now(), settings.repeatWindowMs)) {
      setRepeatNote("Same label read again: ignored");
      if (noteTimer.current !== null) window.clearTimeout(noteTimer.current);
      noteTimer.current = window.setTimeout(() => { setRepeatNote(""); noteTimer.current = null; }, 2000);
      return;
    }
    setRepeatNote("");
    onSubmit(scan);
  };

  useEffect(() => {
    if (disabled) pause.current.cancel();
  }, [disabled]);
  useEffect(() => () => {
    pause.current.cancel();
    if (noteTimer.current !== null) window.clearTimeout(noteTimer.current);
  }, []);

  const onKeyDown = (e: KeyboardEvent<HTMLInputElement>) => {
    const separator = separatorForControlKey(e.key, e.ctrlKey);
    if (separator) {
      e.preventDefault();
      const input = e.currentTarget;
      const start = input.selectionStart ?? current.length;
      const end = input.selectionEnd ?? start;
      set(current.slice(0, start) + separator + current.slice(end));
      pause.current.key(performance.now(), settings.endPauseMs, () => currentRef.current, emit);
      return;
    }
    if (e.key === "Enter") {
      e.preventDefault();
      const entered = pause.current.enter(current);
      if (!current.trim()) return;
      emit(entered);
      return;
    }
    if (e.key.length === 1) pause.current.key(performance.now(), settings.endPauseMs, () => currentRef.current, emit);
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
      {!repeatNote && hint && <span className="scan-hint">{hint}</span>}
      <span className={repeatNote ? "scan-hint" : "sr-only"} aria-live="polite">{repeatNote}</span>
    </div>
  );
}
