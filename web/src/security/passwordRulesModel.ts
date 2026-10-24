/** A company's password rules as the server applies them (ADR-0008); the strictest of a person's companies. */
export interface PasswordRuleSet {
  minimumLength: number;
  requireUppercase: boolean;
  requireLowercase: boolean;
  requireDigit: boolean;
  requireSymbol: boolean;
}

export const DEFAULT_PASSWORD_RULES: PasswordRuleSet = { minimumLength: 12, requireUppercase: true, requireLowercase: true, requireDigit: true, requireSymbol: true };
export const SHORTEST_MINIMUM = 8;
export const LONGEST_MINIMUM = 64;

export interface RuleCheck { key: string; label: string; met: boolean }

// Mirrors the server's classes so the hints tick as the person types; the server stays the only judge. A letter from a
// script without capitals counts as both capital and small, as it does on the server.
const UPPER = /[\p{Lu}\p{Lt}\p{Lo}\p{Lm}]/u;
const LOWER = /[\p{Ll}\p{Lo}\p{Lm}]/u;
const DIGIT = /\p{Nd}/u;
const SYMBOL = /[^\p{L}\p{Nd}\p{M}]/u;

export function ruleChecks(password: string, rules: PasswordRuleSet): RuleCheck[] {
  const length = [...password].length;
  const checks: RuleCheck[] = [{ key: "length", label: `At least ${rules.minimumLength} characters`, met: length >= rules.minimumLength }];
  if (rules.requireUppercase) checks.push({ key: "upper", label: "A capital letter", met: UPPER.test(password) });
  if (rules.requireLowercase) checks.push({ key: "lower", label: "A small letter", met: LOWER.test(password) });
  if (rules.requireDigit) checks.push({ key: "digit", label: "A number", met: DIGIT.test(password) });
  if (rules.requireSymbol) checks.push({ key: "symbol", label: "A symbol, such as ! # % or a space", met: SYMBOL.test(password) });
  return checks;
}

export function minimumLengthValid(value: number): boolean {
  return Number.isInteger(value) && value >= SHORTEST_MINIMUM && value <= LONGEST_MINIMUM;
}
