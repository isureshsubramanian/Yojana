import type { ReactNode } from "react";
import { Icon } from "../shell/Icons";

/** Shared confirmation/form dialog used by replacement and refurbishment command screens. */
export function ActionDialog({ title, children, actions, onCancel, wide }: {
  title: string; children: ReactNode; actions: ReactNode; onCancel: () => void; wide?: boolean | undefined;
}) {
  return <div className="action-dialog-backdrop" role="presentation" onMouseDown={(event) => { if (event.target === event.currentTarget) onCancel(); }}>
    <section className={`action-dialog${wide ? " wide" : ""}`} role="dialog" aria-modal="true" aria-labelledby="action-dialog-title">
      <div className="card-header"><h2 id="action-dialog-title">{title}</h2><button type="button" className="icon-btn" aria-label="Close" onClick={onCancel}><Icon name="x" /></button></div>
      <div className="card-body">{children}<div className="actions"><button type="button" className="btn" onClick={onCancel}>Cancel</button>{actions}</div></div>
    </section>
  </div>;
}
