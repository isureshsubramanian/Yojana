import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import * as z from "zod/mini";
import { erpBomCompletenessSettings } from "../src/settings/itemSerialisationApi.ts";

test("ERP BOM completeness setting is runtime checked", () => {
  assert.equal(z.safeParse(erpBomCompletenessSettings,
    { checkAgainstErpBom: false, version: "AAAAAAAAB9E=" }).success, true);
  assert.equal(z.safeParse(erpBomCompletenessSettings,
    { checkAgainstErpBom: "off", version: "AAAAAAAAB9E=" }).success, false);
});

test("item serialisation screen warns that the optional BOM check can block release", () => {
  const source = readFileSync(new URL("../src/settings/ItemSerialisation.tsx", import.meta.url), "utf8");
  assert.match(source, /Check as-built against the ERP's bill of materials/);
  assert.match(source, /blocks release of units missing a bill-of-materials part/);
});
