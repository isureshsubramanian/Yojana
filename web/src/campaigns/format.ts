import type { Tone } from "../components";

export function campaignTone(status: string): Tone {
  if (status === "active") return "ok";
  if (status === "selected") return "info";
  if (status === "closed") return "neutral";
  return "warn";
}

export function repairPresentation(claimStatus: string | null, custodyKind: string | null): { label: string; tone: Tone; detail: string | null } {
  if (claimStatus) return { label: title(claimStatus), tone: claimStatus === "denied" ? "bad" : claimStatus === "closed" || claimStatus === "settled" ? "ok" : "info", detail: null };
  if (custodyKind === "in_repair") return { label: "Fix in repair", tone: "warn", detail: null };
  if (["warehouse", "bin", "at_3pl"].includes(custodyKind ?? "")) return { label: "Fix before ship", tone: "warn", detail: null };
  return { label: "Open", tone: "neutral", detail: null };
}

export function title(value: string): string {
  const text = value.replace(/_/g, " ");
  return text ? text[0]!.toUpperCase() + text.slice(1) : "—";
}

export function pageNumber(skip: number, take: number): number { return Math.floor(skip / take) + 1; }
