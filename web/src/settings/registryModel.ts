export interface FilterableSetting { name: string; description: string }
export interface RoleSetting { roles: string[] }
export interface CurrentValueSetting { path: string; currentValue?: (signal: AbortSignal) => Promise<string> }
export interface SummaryLineValue { key: string; counts: Record<string, number>; attention: string | null }
export interface SettingsValue { text: string; attention?: string }
export interface SummaryValueSetting extends CurrentValueSetting {
  summaryKey?: string;
  summaryValue?: (line: SummaryLineValue) => SettingsValue;
}

export function filterSettings<T extends FilterableSetting>(entries: readonly T[], query: string): T[] {
  const text = query.trim().toLocaleLowerCase();
  return text ? entries.filter(entry => `${entry.name} ${entry.description}`.toLocaleLowerCase().includes(text)) : [...entries];
}

export function visibleSettings<T extends RoleSetting>(entries: readonly T[], roles: readonly string[]): T[] {
  return entries.filter(entry => entry.roles.some(role => roles.includes(role)));
}

export async function loadCurrentValues(entries: readonly CurrentValueSetting[], signal: AbortSignal): Promise<Record<string, string>> {
  const reads = entries.filter(entry => entry.currentValue).map(async entry => {
    try { return [entry.path, await entry.currentValue!(signal)] as const; }
    catch (error) { if (signal.aborted) throw error; return [entry.path, "—"] as const; }
  });
  return Object.fromEntries(await Promise.all(reads));
}

export async function loadSettingsValues(
  entries: readonly SummaryValueSetting[],
  readSummary: (signal: AbortSignal) => Promise<{ lines: SummaryLineValue[] }>,
  signal: AbortSignal,
): Promise<Record<string, SettingsValue>> {
  const currentPromise = loadCurrentValues(entries, signal);
  const summaryEntries = entries.filter(entry => entry.summaryKey && entry.summaryValue);
  const summaryPromise = summaryEntries.length === 0 ? Promise.resolve([] as SummaryLineValue[]) : readSummary(signal)
    .then(response => response.lines)
    .catch(error => { if (signal.aborted) throw error; return []; });
  const [current, lines] = await Promise.all([currentPromise, summaryPromise]);
  const values: Record<string, SettingsValue> = Object.fromEntries(
    Object.entries(current).map(([path, text]) => [path, { text }]),
  );
  const byKey = new Map(lines.map(line => [line.key, line]));
  for (const entry of summaryEntries) {
    const line = byKey.get(entry.summaryKey!);
    values[entry.path] = line ? entry.summaryValue!(line) : { text: "—" };
  }
  return values;
}
