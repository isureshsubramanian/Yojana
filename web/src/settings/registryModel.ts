export interface FilterableSetting { name: string; description: string }
export interface RoleSetting { roles: string[] }
export interface CurrentValueSetting { path: string; currentValue?: (signal: AbortSignal) => Promise<string> }

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
