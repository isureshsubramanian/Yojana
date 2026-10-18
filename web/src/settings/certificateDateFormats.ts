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
  timePattern: CertificateTimePattern): string {
  const locale = supportedLocale(language);
  const parts = new Intl.DateTimeFormat(locale, {
    day: "numeric", month: "short", year: "numeric", timeZone: "UTC",
  }).formatToParts(example);
  const part = (type: Intl.DateTimeFormatPartTypes) => parts.find((value) => value.type === type)?.value ?? "";
  const day = part("day").padStart(2, "0");
  const month = String(example.getUTCMonth() + 1).padStart(2, "0");
  const year = String(example.getUTCFullYear());
  const date = datePattern === "d MMM yyyy" ? `${Number(day)} ${part("month")} ${year}`
    : datePattern === "dd/MM/yyyy" ? `${day}/${month}/${year}`
      : datePattern === "MM/dd/yyyy" ? `${month}/${day}/${year}`
        : `${year}-${month}-${day}`;
  const time = timePattern === "HH:mm" ? "13:45" : twelveHourExample(locale);
  return `${date}, ${time} UTC`;
}

export function certificateLanguageName(language: string): string {
  try { return new Intl.DisplayNames([supportedLocale(language)], { type: "language" }).of(language) ?? language; }
  catch { return language; }
}

function twelveHourExample(locale: string): string {
  const parts = new Intl.DateTimeFormat(locale, {
    hour: "numeric", minute: "2-digit", hour12: true, timeZone: "UTC",
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
