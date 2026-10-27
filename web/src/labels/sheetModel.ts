import type { LabelSheetProfile } from "./types";

export interface SheetFit { fits: boolean; horizontalOverflowMm: number; verticalOverflowMm: number }

export function pageDimensions(pageSize: "A4" | "Letter"): { width: number; height: number } {
  return pageSize === "A4" ? { width: 210, height: 297 } : { width: 215.9, height: 279.4 };
}

export function sheetFit(profile: LabelSheetProfile): SheetFit {
  const page = pageDimensions(profile.pageSize);
  const usedWidth = profile.marginLeftMm + profile.marginRightMm + profile.columns * profile.labelWidthMm + (profile.columns - 1) * profile.horizontalGapMm;
  const usedHeight = profile.marginTopMm + profile.marginBottomMm + profile.rows * profile.labelHeightMm + (profile.rows - 1) * profile.verticalGapMm;
  const horizontalOverflowMm = Math.max(0, usedWidth - page.width);
  const verticalOverflowMm = Math.max(0, usedHeight - page.height);
  return { fits: horizontalOverflowMm === 0 && verticalOverflowMm === 0, horizontalOverflowMm, verticalOverflowMm };
}

export function sheetName(profile: LabelSheetProfile): string {
  return `${profile.pageSize} · ${profile.columns} × ${profile.rows} · ${number(profile.labelWidthMm)} × ${number(profile.labelHeightMm)} mm`;
}

function number(value: number): string { return Number(value.toFixed(2)).toString(); }
