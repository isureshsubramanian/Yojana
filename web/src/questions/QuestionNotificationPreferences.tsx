import { useEffect, useState } from "react";
import { questionsApi } from "./api";
import type { QuestionNotificationMode, QuestionNotificationPreference } from "./types";

export function QuestionNotificationPreferences() {
  const [preference, setPreference] = useState<QuestionNotificationPreference | null>(null);
  const [error, setError] = useState<string | null>(null); const [busy, setBusy] = useState(false);
  useEffect(() => { const controller = new AbortController(); void questionsApi.notificationPreference(controller.signal)
    .then(setPreference).catch(() => { if (!controller.signal.aborted) setError("Question notification settings could not be loaded."); });
    return () => controller.abort(); }, []);
  const save = async (value: string) => {
    setBusy(true); setError(null);
    try { setPreference(await questionsApi.updateNotificationPreference(value ? value as QuestionNotificationMode : null)); }
    catch { setError("Question notification settings could not be saved."); }
    finally { setBusy(false); }
  };
  return <section className="card" aria-labelledby="account-question-notifications"><div className="card-header"><h2 id="account-question-notifications">Question notifications</h2><span className="meta">E-mail</span></div><div className="card-body stack"><label className="field"><span className="lbl">Tell me about question activity</span><select className="select" value={preference?.choice ?? ""} disabled={!preference || busy} onChange={event => void save(event.target.value)}><option value="">Use company defaults</option><option value="immediately">Immediately</option><option value="daily_digest">Daily digest</option><option value="off">Off</option></select></label>{preference && <p className="muted">Company defaults: questions sent to you personally are <strong>{label(preference.personalDefault)}</strong>; questions sent to your roles are in a <strong>{label(preference.roleDefault)}</strong>.</p>}{error && <div className="form-error" role="alert">{error}</div>}</div></section>;
}

const label = (mode: QuestionNotificationMode) => mode === "daily_digest" ? "daily digest" : mode;
