import assert from "node:assert/strict";
import { readdirSync, readFileSync } from "node:fs";
import { join } from "node:path";
import test from "node:test";

const sourceRoot = new URL("../src", import.meta.url).pathname;

function sourceFiles(directory: string): string[] {
  return readdirSync(directory, { withFileTypes: true }).flatMap((entry) => {
    const path = join(directory, entry.name);
    return entry.isDirectory() ? sourceFiles(path) : /\.(ts|tsx)$/.test(entry.name) ? [path] : [];
  });
}

test("web source contains no hard-coded legal entity id", () => {
  const legalEntityGuid = /00000000-0000-0000-0000-000000000001/i;
  const offenders = sourceFiles(sourceRoot).filter((file) => legalEntityGuid.test(readFileSync(file, "utf8")));
  assert.deepEqual(offenders, []);
});

test("accepted chooser screens use the tenant legal-entity hook", () => {
  for (const file of ["units/Receive.tsx", "units/Release.tsx", "units/SerialRanges.tsx", "claims/ClaimIntake.tsx", "entitlements/BulkOperation.tsx"])
    assert.match(readFileSync(join(sourceRoot, file), "utf8"), /useLegalEntities/);
});
