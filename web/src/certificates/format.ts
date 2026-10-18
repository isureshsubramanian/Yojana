import type { Tone } from "../components/types";

export function humanise(value: string | null | undefined): string {
  if (!value) return "—";
  const words = value.replace(/_/g, " ");
  return words.charAt(0).toUpperCase() + words.slice(1);
}
export function fmtDate(value: string | null | undefined): string {
  return value ? new Date(value).toLocaleDateString(undefined, { year: "numeric", month: "short", day: "numeric" }) : "—";
}
export function fmtDateTime(value: string | null | undefined): string {
  return value ? new Date(value).toLocaleString() : "—";
}
export function shortHash(value: string | null | undefined): string {
  return value && value.length > 16 ? `${value.slice(0, 8)}…${value.slice(-8)}` : value ?? "—";
}
export function statusTone(value: string | null | undefined): Tone {
  if (value === "issued" || value === "active") return "ok";
  if (value === "draft" || value === "waiting") return "warn";
  return "neutral";
}
export function formLabel(value: string): string {
  if (value === "coc") return "Certificate of conformity";
  if (value === "dhr_extract") return "Production-history extract";
  return humanise(value);
}
