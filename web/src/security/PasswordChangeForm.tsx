import { useState, type FormEvent } from "react";
import { ApiError } from "../api/client";
import { passwordApi } from "./api";
import { PasswordRules, usePasswordRules } from "./PasswordRules";
import { useSession } from "../session/session";

export function PasswordChangeForm({ onChanged }: { onChanged?: (() => void | Promise<void>) | undefined }) {
  const session = useSession();
  const [currentPassword, setCurrentPassword] = useState("");
  const [newPassword, setNewPassword] = useState("");
  const [confirmation, setConfirmation] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const rules = usePasswordRules({});
  const [reasons, setReasons] = useState<readonly { code: string; message: string }[]>([]);

  const submit = async (event: FormEvent) => {
    event.preventDefault(); setError(null); setReasons([]);
    if (newPassword !== confirmation) { setError("Type the same new password twice."); return; }
    setBusy(true);
    try {
      await passwordApi.change(currentPassword, newPassword);
      setCurrentPassword(""); setNewPassword(""); setConfirmation("");
      await onChanged?.();
    } catch (reason) {
      if (reason instanceof ApiError) {
        setReasons(reason.reasons);
        setError(reason.title === "current_password_wrong" ? "The current password is not correct." : reason.detail ?? "The password could not be changed.");
      } else setError("The password could not be changed.");
    } finally { setBusy(false); }
  };

  return <form className="stack" onSubmit={submit} noValidate>
    <label className="field"><span className="lbl">Current password</span><input className="input" type="password" autoComplete="current-password" required value={currentPassword} onChange={(event) => setCurrentPassword(event.target.value)} /></label>
    <label className="field"><span className="lbl">New password</span><input className="input" type="password" autoComplete="new-password" required value={newPassword} onChange={(event) => setNewPassword(event.target.value)} /></label>
    <label className="field"><span className="lbl">Type it again</span><input className="input" type="password" autoComplete="new-password" required value={confirmation} onChange={(event) => setConfirmation(event.target.value)} /></label>
    <PasswordRules password={newPassword} confirmation={confirmation} rules={rules} email={session.user.email} displayName={session.user.displayName} />
    {reasons.length > 0 && <ul className="note bad" aria-label="Password policy reasons">{reasons.map((reason) => <li key={reason.code}>{reason.message}</li>)}</ul>}
    {error && <div className="form-error" role="alert">{error}</div>}
    <button className="btn primary" type="submit" disabled={busy || !currentPassword || !newPassword || newPassword !== confirmation} aria-busy={busy}>{busy ? "Saving…" : "Change password"}</button>
  </form>;
}
