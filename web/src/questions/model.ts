import type { MyQuestion, QuestionStatus } from "./types";

export const recordKinds = ["unit", "claim", "rma", "refurbishment_work_order", "warranty_policy", "certificate", "import_job", "integration_message"] as const;
export const humanise = (value: string) => value.replaceAll("_", " ").replace(/^./, first => first.toUpperCase());
export const statusLabel = (status: QuestionStatus, replies = 0) => status === "open" ? `Open · ${replies ? `${replies} ${replies === 1 ? "reply" : "replies"}` : "no reply"}` : status === "answered" ? "Answered" : "Closed without answer";
export const statusTone = (status: QuestionStatus): "warn" | "ok" | "neutral" => status === "open" ? "warn" : status === "answered" ? "ok" : "neutral";
export const initials = (name: string) => name.split(/\s+/).filter(Boolean).slice(0, 2).map(part => part[0]?.toUpperCase()).join("");
export const recordPath = (question: Pick<MyQuestion, "recordKind" | "recordId">) => ({ unit: `/units/${question.recordId}`, claim: `/claims/${question.recordId}`, rma: `/returns/${question.recordId}`, refurbishment_work_order: `/refurbishments/${question.recordId}`, certificate: `/certificates/${question.recordId}` } as Record<string, string>)[question.recordKind] ?? null;
