import type { Kpi } from "./types";

export function KpiStrip({ kpis, label = "Key figures" }: { kpis: Kpi[]; label?: string }) {
  return (
    <div className="kpis" aria-label={label}>
      {kpis.map((k) => (
        <div className="card kpi" key={k.label}>
          <div className="label">{k.label}</div>
          <div className="value">{k.value}</div>
          {k.detail && <div className={`delta${k.trend === "up" ? " up" : k.trend === "down" ? " down" : ""}`}>{k.detail}</div>}
        </div>
      ))}
    </div>
  );
}
