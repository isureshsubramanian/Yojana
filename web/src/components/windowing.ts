// Row windowing for the grid: only the rows inside the scroll viewport (plus an overscan) are in the DOM.
// Pure so it can be unit-tested; row height is fixed per density (the table's --row-h token).

export interface Window { start: number; end: number; topPad: number; bottomPad: number }

export function windowRows(count: number, rowHeight: number, scrollTop: number, viewportHeight: number, overscan = 6): Window {
  if (count === 0) return { start: 0, end: 0, topPad: 0, bottomPad: 0 };
  const first = Math.max(0, Math.floor(scrollTop / rowHeight) - overscan);
  const visible = Math.ceil(viewportHeight / rowHeight) + overscan * 2;
  const end = Math.min(count, first + visible);
  return { start: first, end, topPad: first * rowHeight, bottomPad: (count - end) * rowHeight };
}
