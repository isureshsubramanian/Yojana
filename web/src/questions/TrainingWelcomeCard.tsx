import { useEffect, useState } from "react";
import { Link } from "react-router";
import { trainingApi } from "./api";
import type { TrainingWelcome } from "./types";

export function TrainingWelcomeCard() {
  const [card, setCard] = useState<TrainingWelcome | null>(null);
  useEffect(() => { const controller = new AbortController(); void trainingApi.welcome(controller.signal)
    .then(value => setCard(value ?? null)).catch(() => undefined); return () => controller.abort(); }, []);
  if (!card) return null;
  const dismiss = async () => { await trainingApi.dismissWelcome(card.pathId); setCard(null); };
  return <section className="card training-welcome" aria-labelledby="training-welcome-title"><div className="card-body"><div><div className="eyebrow">Your new-team-member training path</div><h2 id="training-welcome-title">Welcome to {card.roleLabel}</h2><p>{card.introduction}</p><p className="muted">{card.readEntries} of {card.entries} entries read. You can return any time from Questions → Training paths.</p></div><div className="actions"><button className="btn" type="button" onClick={() => void dismiss()}>Later</button><Link className="btn primary" to={`/knowledge/training?role=${encodeURIComponent(card.role)}`}>Start reading</Link></div></div></section>;
}
