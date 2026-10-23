export function extensionFigure(value: number | null): string {
  return value === null ? "—" : value.toLocaleString();
}

export function pollInterval(seconds: number | null): string {
  if (seconds === null) return "—";
  if (seconds % 3600 === 0) return `every ${seconds / 3600} hr`;
  if (seconds % 60 === 0) return `every ${seconds / 60} min`;
  return `every ${seconds} sec`;
}

export function extensionVersionDetail(installed: string, shipped: string): string {
  return installed === shipped ? "this release ships the installed version" : `${shipped} ships with this release`;
}
