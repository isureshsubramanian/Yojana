import { tenantZoneAbbreviation } from "../components/tenantTimeFormat.ts";

export type CertificateDatePattern = "d MMM yyyy" | "dd/MM/yyyy" | "MM/dd/yyyy" | "yyyy-MM-dd";
export type CertificateTimePattern = "HH:mm" | "h:mm tt";

export interface CertificateDateFormatChoice {
  language: string;
  datePattern: CertificateDatePattern;
  timePattern: CertificateTimePattern;
}

export const certificateDatePatterns: readonly CertificateDatePattern[] = [
  "d MMM yyyy", "dd/MM/yyyy", "MM/dd/yyyy", "yyyy-MM-dd",
];

export const certificateTimePatterns: ReadonlyArray<{ value: CertificateTimePattern; label: string }> = [
  { value: "HH:mm", label: "24-hour" },
  { value: "h:mm tt", label: "12-hour" },
];

const example = new Date(Date.UTC(2026, 9, 18, 13, 45));

export function certificateDateTimeExample(language: string, datePattern: CertificateDatePattern,
  timePattern: CertificateTimePattern, timeZone = "UTC"): string {
  const locale = supportedLocale(language);
  const parts = new Intl.DateTimeFormat(locale, {
    day: "numeric", month: "short", year: "numeric", timeZone,
  }).formatToParts(example);
  const part = (type: Intl.DateTimeFormatPartTypes) => parts.find((value) => value.type === type)?.value ?? "";
  const day = part("day").padStart(2, "0");
  const month = part("month").match(/^\d+$/) ? part("month").padStart(2, "0") : new Intl.DateTimeFormat("en", { month: "2-digit", timeZone }).format(example);
  const year = part("year");
  const date = datePattern === "d MMM yyyy" ? `${Number(day)} ${part("month")} ${year}`
    : datePattern === "dd/MM/yyyy" ? `${day}/${month}/${year}`
      : datePattern === "MM/dd/yyyy" ? `${month}/${day}/${year}`
        : `${year}-${month}-${day}`;
  const time = timePattern === "HH:mm" ? new Intl.DateTimeFormat("en-GB", { hour: "2-digit", minute: "2-digit", hour12: false, timeZone }).format(example) : twelveHourExample(locale, timeZone);
  const zoneName = tenantZoneAbbreviation(example, timeZone);
  return `${date}, ${time} ${zoneName}`;
}

export function certificateLanguageName(language: string): string {
  try { return new Intl.DisplayNames([supportedLocale(language)], { type: "language" }).of(language) ?? language; }
  catch { return language; }
}

function twelveHourExample(locale: string, timeZone: string): string {
  const parts = new Intl.DateTimeFormat(locale, {
    hour: "numeric", minute: "2-digit", hour12: true, timeZone,
  }).formatToParts(example);
  const hour = parts.find((value) => value.type === "hour")?.value ?? "1";
  const minute = parts.find((value) => value.type === "minute")?.value ?? "45";
  const dayPeriod = parts.find((value) => value.type === "dayPeriod")?.value ?? "PM";
  return `${hour}:${minute} ${dayPeriod}`;
}

function supportedLocale(language: string): string {
  try { return Intl.DateTimeFormat.supportedLocalesOf([language])[0] ?? "en-GB"; }
  catch { return "en-GB"; }
}
