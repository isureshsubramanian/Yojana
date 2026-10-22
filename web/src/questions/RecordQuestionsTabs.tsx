import { useEffect, useState } from "react";
import { Link, useSearchParams } from "react-router";
import { questionsApi } from "./api";

export function useQuestionsTab(): boolean { return useSearchParams()[0].get("tab") === "questions"; }

export function QuestionTabLink({ recordKind, recordId, search }: { recordKind: string; recordId: string; search: URLSearchParams }) {
  const [count, setCount] = useState<number | null>(null);
  useEffect(() => { const controller = new AbortController(); void questionsApi.onRecord(recordKind, recordId, undefined, undefined, 1, controller.signal).then(page => setCount(page.counts.open)).catch(() => setCount(null)); return () => controller.abort(); }, [recordKind, recordId]);
  const next = new URLSearchParams(search); next.set("tab", "questions");
  return <Link to={`?${next}`} aria-current={search.get("tab") === "questions" ? "page" : undefined}>Questions <span className="muted">{count === null ? "…" : `${count} open`}</span></Link>;
}

export function RecordQuestionsTabs({ recordKind, recordId }: { recordKind: string; recordId: string }) {
  const [search] = useSearchParams(); const questions = search.get("tab") === "questions";
  const detail = new URLSearchParams(search); detail.delete("tab");
  return <nav className="tabs question-record-tabs" aria-label="Record sections"><Link to={`?${detail}`} aria-current={!questions ? "page" : undefined}>Details</Link><QuestionTabLink recordKind={recordKind} recordId={recordId} search={search} /></nav>;
}
