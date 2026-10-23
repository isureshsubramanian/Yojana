import { useEffect, useMemo, useState } from "react";
import { api } from "../api/client";
import { useSession } from "../session/session";
import { tenantZoneAbbreviation } from "./tenantTimeFormat";

interface TenantTimeZoneResponse { tenantTimeZone: string | null }

const zones = new Map<string, string>();

/** One presentation boundary for screen date-times. Storage remains UTC; every instant is shown in the tenant zone. */
export function useTenantTime(): TenantTimeFormatter {
  const session = useSession();
  const tenant = session.tenant?.code ?? "";
  const [zone, setZone] = useState(() => zones.get(tenant) ?? "UTC");
  useEffect(() => {
    const cached = zones.get(tenant);
    if (cached) { setZone(cached); return; }
    const controller = new AbortController();
    api<TenantTimeZoneResponse>("/api/settings/time-zone", { signal: controller.signal })
      .then((result) => { const next = result.tenantTimeZone ?? "UTC"; zones.set(tenant, next); setZone(next); })
      .catch((reason) => { if (!(reason instanceof DOMException && reason.name === "AbortError")) setZone("UTC"); });
    return () => controller.abort();
  }, [tenant]);
  return useMemo(() => tenantTimeFormatter(zone), [zone]);
}

export interface TenantTimeFormatter {
  zone: string;
  zoneLabel: string;
  date: (value: string | null | undefined) => string;
  dateTime: (value: string | null | undefined) => string;
  time: (value: string | null | undefined) => string;
  localInput: (value: string | null | undefined) => string;
  fromLocalInput: (value: string) => string;
  dayBounds: (fromDay: string, throughDay: string) => { from: string; to: string };
}

export function tenantTimeFormatter(zone: string): TenantTimeFormatter {
  const safeZone = validZone(zone) ? zone : "UTC";
  return {
    zone: safeZone,
    zoneLabel: safeZone === "UTC" || safeZone === "Etc/UTC" ? "UTC" : safeZone,
    date: (value) => format(value, safeZone, false, false),
    dateTime: (value) => format(value, safeZone, true, true),
    time: (value) => formatTime(value, safeZone),
    localInput: (value) => localInput(value, safeZone),
    fromLocalInput: (value) => zonedLocalToIso(value, safeZone),
    dayBounds: (fromDay, throughDay) => ({
      from: zonedLocalToIso(`${fromDay}T00:00`, safeZone),
      to: zonedLocalToIso(`${addDays(throughDay || fromDay, 1)}T00:00`, safeZone),
    }),
  };
}

// Hidden coupling: server timestamps arrive with a "Z" suffix because UtcDateTimeConverter (Yojana.Platform/Data) marks
// every stored DateTime as UTC; new Date() would read a value without it as the browser's local time.
function format(value: string | null | undefined, zone: string, includeTime: boolean, includeZone: boolean): string {
  if (!value) return "—";
  const dateOnly = /^\d{4}-\d{2}-\d{2}$/.test(value);
  const date = new Date(dateOnly ? `${value}T00:00:00Z` : value);
  if (Number.isNaN(date.getTime())) return "—";
  const rendered = new Intl.DateTimeFormat(undefined, {
    day: "numeric", month: "short", year: "numeric",
    ...(includeTime ? { hour: "2-digit", minute: "2-digit" } : {}),
    timeZone: dateOnly ? "UTC" : zone,
  }).format(date);
  return includeZone ? `${rendered} ${tenantZoneAbbreviation(date, zone)}` : rendered;
}

function formatTime(value: string | null | undefined, zone: string): string {
  if (!value) return "—";
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? "—" : `${new Intl.DateTimeFormat(undefined, {
    hour: "2-digit", minute: "2-digit", timeZone: zone,
  }).format(date)} ${tenantZoneAbbreviation(date, zone)}`;
}

function localInput(value: string | null | undefined, zone: string): string {
  if (!value) return "";
  const parts = partsInZone(new Date(value), zone);
  return `${parts.year}-${two(parts.month)}-${two(parts.day)}T${two(parts.hour)}:${two(parts.minute)}`;
}

function zonedLocalToIso(value: string, zone: string): string {
  const match = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2})/.exec(value);
  if (!match) return new Date(value).toISOString();
  const desired = { year: Number(match[1]), month: Number(match[2]), day: Number(match[3]), hour: Number(match[4]), minute: Number(match[5]) };
  const desiredUtc = Date.UTC(desired.year, desired.month - 1, desired.day, desired.hour, desired.minute);
  let instant = new Date(desiredUtc);
  for (let pass = 0; pass < 2; pass += 1) {
    const actual = partsInZone(instant, zone);
    const actualUtc = Date.UTC(actual.year, actual.month - 1, actual.day, actual.hour, actual.minute);
    instant = new Date(instant.getTime() + desiredUtc - actualUtc);
  }
  return instant.toISOString();
}

function partsInZone(value: Date, zone: string): { year: number; month: number; day: number; hour: number; minute: number } {
  const parts = new Intl.DateTimeFormat("en-CA", {
    year: "numeric", month: "2-digit", day: "2-digit", hour: "2-digit", minute: "2-digit",
    hourCycle: "h23", timeZone: zone,
  }).formatToParts(value);
  const number = (type: Intl.DateTimeFormatPartTypes) => Number(parts.find((part) => part.type === type)?.value ?? 0);
  return { year: number("year"), month: number("month"), day: number("day"), hour: number("hour"), minute: number("minute") };
}

function addDays(day: string, count: number): string {
  const date = new Date(`${day}T00:00:00Z`); date.setUTCDate(date.getUTCDate() + count);
  return date.toISOString().slice(0, 10);
}
function validZone(zone: string): boolean { try { new Intl.DateTimeFormat(undefined, { timeZone: zone }); return true; } catch { return false; } }
function two(value: number): string { return String(value).padStart(2, "0"); }
