import { useState } from "react";
import { ApiError } from "../api/client";
import { ActionDialog } from "../components";

export const MODEL_NUMBER_RULE = "serial_is_model_number";

export function isModelNumberRefusal(reason: unknown): reason is ApiError {
  return reason instanceof ApiError && reason.rule === MODEL_NUMBER_RULE;
}

export function ModelNumberConfirmation({ refusal, busy, onDismiss, onConfirm }: {
  refusal: ApiError | null;
  busy: boolean;
  onDismiss: () => void;
  onConfirm: (reason: string) => Promise<void>;
}) {
  const [open, setOpen] = useState(false);
  const [reason, setReason] = useState("");
  if (!refusal) return null;
  const close = () => { setOpen(false); setReason(""); };
  return <>
    <div className="note bad" role="alert"><strong>{refusal.detail ?? refusal.message}</strong>&nbsp;
      <button type="button" className="btn" onClick={() => setOpen(true)}>This really is the serial</button>
      <button type="button" className="btn ghost" onClick={onDismiss}>Scan again</button>
    </div>
    {open && <ActionDialog title="Confirm this really is the serial" onCancel={close} actions={
      <button type="button" className="btn primary" disabled={busy || !reason.trim()} onClick={() => void onConfirm(reason.trim()).then(close)}>
        {busy ? "Recording…" : "Confirm serial"}
      </button>
    }>
      <p className="muted">The value matches the item code or a model number. Record why the physical label proves it is this unit’s serial.</p>
      <label className="field"><span className="lbl">Reason</span><textarea className="textarea" autoFocus maxLength={500} value={reason} onChange={event => setReason(event.target.value)} /></label>
    </ActionDialog>}
  </>;
}
