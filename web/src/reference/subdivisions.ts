import { COUNTRY_CODES } from "./countries.ts";

const countryCodes = new Set(COUNTRY_CODES);

export function subdivisionParts(value: string | null | undefined): { country: string; suffix: string } {
  const [country = "", suffix = ""] = (value ?? "").toUpperCase().split("-", 2);
  return { country, suffix };
}

export function isSubdivisionSuffix(value: string): boolean { return /^[A-Z0-9]{1,3}$/.test(value); }

export function isSubdivisionCode(value: string | null | undefined): boolean {
  const { country, suffix } = subdivisionParts(value);
  return countryCodes.has(country) && isSubdivisionSuffix(suffix);
}
