import type { RefurbishmentPolicy } from "../refurbishments/types.ts";

// Rows whose item class (ignoring case and spaces) repeats an earlier row; the first one stays valid.
export function duplicateRows(policy: RefurbishmentPolicy): Set<number> {
  const seen = new Set<string>(); const duplicates = new Set<number>();
  policy.itemClasses.forEach((row, index) => { const key = row.itemClass.trim().toLowerCase(); if (!key) return; if (seen.has(key)) duplicates.add(index); else seen.add(key); });
  return duplicates;
}

// Why Save is unavailable, in words, so a disabled Save is never silent.
export function policyProblems(policy: RefurbishmentPolicy, duplicates: Set<number>): string[] {
  const problems: string[] = [];
  if (duplicates.size > 0) problems.push(`${[...new Set([...duplicates].map((index) => policy.itemClasses[index]!.itemClass.trim()))].join(", ")} ${duplicates.size === 1 ? "is" : "are"} overridden more than once. Remove the duplicate row or change its item class.`);
  if (policy.itemClasses.some((row) => !row.itemClass.trim())) problems.push("Every override needs an item class.");
  if (policy.itemClasses.some((row) => !Number.isInteger(row.maxCycles) || row.maxCycles < 1 || row.maxCycles > 100)) problems.push("Max cycles must be a whole number from 1 to 100.");
  if (policy.itemClasses.some((row) => row.identity === "new_unit" && !row.replacementPolicyCode?.trim())) problems.push("A new derived unit needs a replacement policy code.");
  return problems;
}
