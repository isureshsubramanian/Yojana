// ISO 4217 currency codes and names come from the browser's Intl data, like countries.ts; nothing is bundled.
export interface CurrencyOption { code: string; label: string }

export function currencyName(code: string): string {
  try { return new Intl.DisplayNames(undefined, { type: "currency" }).of(code) ?? code; } catch { return code; }
}

// Every ISO 4217 code the browser knows, sorted by code; a saved value the browser does not list is kept so it still shows.
export function currencyOptions(current?: string | null): CurrencyOption[] {
  let codes: string[] = [];
  try { codes = Intl.supportedValuesOf("currency"); } catch { codes = []; }
  const all = new Set(codes.map((code) => code.toUpperCase()));
  if (current) all.add(current.toUpperCase());
  return [...all].sort().map((code) => ({ code, label: `${code} · ${currencyName(code)}` }));
}
