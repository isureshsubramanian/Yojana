// Scan-first capture (ADR-0001 identity rule 11). A keyboard-wedge scanner types the barcode and sends
// Enter; the FNC1 separator inside a GS1 element string arrives as a control key that a text field drops.
// This module recovers it as the ASCII group separator so the server can parse the string; nothing else
// is interpreted here (symbology prefixes, application identifiers and check digits are the server's).

export const GROUP_SEPARATOR = "\u001d";

export interface ScanningPolicy { endPauseMs: number; repeatWindowMs: number }
export const DEFAULT_SCANNING_POLICY: ScanningPolicy = { endPauseMs: 300, repeatWindowMs: 1500 };

export class ScanSettingsSessionCache {
  private readonly reads = new Map<string, Promise<ScanningPolicy>>();

  load(sessionKey: string, read: () => Promise<ScanningPolicy>): Promise<ScanningPolicy> {
    const existing = this.reads.get(sessionKey);
    if (existing) return existing;
    const pending = read().catch(() => DEFAULT_SCANNING_POLICY);
    this.reads.set(sessionKey, pending);
    return pending;
  }

  clear(): void { this.reads.clear(); }
}

/** Keys a wedge may send for FNC1: Ctrl+] (GS), sometimes Ctrl+^ (RS) or Ctrl+_ (US) for other separators. */
const controlSeparators: Record<string, string> = { "]": "\u001d", "^": "\u001e", _: "\u001f" };

export function separatorForControlKey(key: string, ctrlKey: boolean): string | null {
  return ctrlKey ? controlSeparators[key] ?? null : null;
}

/** Some wedge configurations emit the separator as a literal token instead of a control key. */
const literalTokens = [/\{GS\}/g, /<GS>/g, /\\x1d/gi, /\\u001d/gi];

export function recoverGroupSeparators(text: string): string {
  let out = text;
  for (const token of literalTokens) out = out.replace(token, GROUP_SEPARATOR);
  return out;
}

export interface ScanSubmission {
  /** The text with separators recovered; what the server receives. */
  value: string;
  /** "scan" when the keystrokes arrived at scanner speed, "typed" otherwise. */
  source: "scan" | "typed";
  /** The boundary that completed this submission. */
  endedBy: "enter" | "pause";
  hasGroupSeparator: boolean;
}

/** Scanners type at well under this interval per character; people do not. */
export const SCAN_BURST_MS = 35;

/** Tracks keystroke timing so Enter can be classified as a scan or a typed submission. */
export class ScanTimer {
  private lastAt = 0;
  private burstChars = 0;
  private typedChars = 0;

  key(now: number): void {
    const gap = now - this.lastAt;
    this.lastAt = now;
    if (gap <= SCAN_BURST_MS) this.burstChars++;
    else this.typedChars++;
  }

  /** A submission is a scan when most characters arrived in bursts. */
  classify(length: number): "scan" | "typed" {
    const result = length >= 4 && this.burstChars >= Math.max(3, Math.floor(length * 0.6)) ? "scan" : "typed";
    this.reset();
    return result;
  }

  reset(): void {
    this.lastAt = 0;
    this.burstChars = 0;
    this.typedChars = 0;
  }
}

export function submission(raw: string, source: "scan" | "typed", endedBy: "enter" | "pause"): ScanSubmission {
  const value = recoverGroupSeparators(raw).trim();
  return { value, source, endedBy, hasGroupSeparator: value.includes(GROUP_SEPARATOR) };
}

export interface ScanTimeouts {
  set(callback: () => void, delayMs: number): unknown;
  clear(handle: unknown): void;
}

/** Owns the pause boundary so it can be tested with a deterministic clock rather than a browser. */
export class PauseEndedScan {
  private handle: unknown = null;
  private readonly timer: ScanTimer;
  private readonly timeouts: ScanTimeouts;

  constructor(timer: ScanTimer, timeouts: ScanTimeouts) {
    this.timer = timer;
    this.timeouts = timeouts;
  }

  key(now: number, pauseMs: number, value: () => string, onScan: (scan: ScanSubmission) => void): void {
    this.timer.key(now);
    if (this.handle !== null) this.timeouts.clear(this.handle);
    this.handle = this.timeouts.set(() => {
      this.handle = null;
      const raw = value();
      if (raw.trim() && this.timer.classify(raw.length) === "scan") onScan(submission(raw, "scan", "pause"));
    }, pauseMs);
  }

  enter(raw: string): ScanSubmission {
    this.cancel(false);
    return submission(raw, this.timer.classify(raw.length), "enter");
  }

  cancel(reset = true): void {
    if (this.handle !== null) this.timeouts.clear(this.handle);
    this.handle = null;
    if (reset) this.timer.reset();
  }
}

/** Exact, per-input repeat suppression. Zero disables it. */
export class ScanRepeatSuppressor {
  private previous: { value: string; at: number } | null = null;

  accept(value: string, now: number, windowMs: number): boolean {
    if (windowMs > 0 && this.previous?.value === value && now - this.previous.at <= windowMs) return false;
    this.previous = { value, at: now };
    return true;
  }
}

export function isUrlScanFormat(format: number | string): boolean {
  return format === 3 || String(format).toLowerCase() === "url";
}

/** For display only: the invisible separator shown as a visible token. */
export function displayScan(value: string): string {
  return value.replaceAll(GROUP_SEPARATOR, "⟨GS⟩");
}
