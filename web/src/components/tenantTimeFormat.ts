/** Uses a stable locale whose common IANA zones have the same short names as printed certificates. */
export function tenantZoneAbbreviation(value: Date, zone: string): string {
  return new Intl.DateTimeFormat("en-IN", { timeZone: zone, timeZoneName: "short" }).formatToParts(value)
    .find((part) => part.type === "timeZoneName")?.value ?? zone;
}
