import type { NumberingMode, NumberingSeriesSetting } from "./numberingApi";

export interface NumberingChange { series: string; mode: NumberingMode }

export function numberingChanges(saved: readonly NumberingSeriesSetting[], draft: readonly NumberingSeriesSetting[]): NumberingChange[] {
  return draft.filter(row => saved.find(original => original.series === row.series)?.mode !== row.mode).map(row => ({ series: row.series, mode: row.mode }));
}
