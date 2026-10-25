import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import { fieldCatalogueEntrySchema } from "../src/certificates/types.ts";
import {
  certificateDatePatterns,
  certificateDateTimeExample,
  certificateTimePatterns,
} from "../src/settings/certificateDateFormats.ts";
import { releaseRecordIssuePointSettings } from "../src/settings/certificateSettingsApi.ts";
import * as z from "zod/mini";

test("certificate date setting offers every approved fixed pattern with a live example", () => {
  assert.deepEqual(certificateDatePatterns, ["d MMM yyyy", "dd/MM/yyyy", "MM/dd/yyyy", "yyyy-MM-dd"]);
  assert.equal(certificateDateTimeExample("en", "d MMM yyyy", "HH:mm"), "18 Oct 2026, 13:45 UTC");
  assert.equal(certificateDateTimeExample("en", "dd/MM/yyyy", "HH:mm"), "18/10/2026, 13:45 UTC");
  assert.equal(certificateDateTimeExample("en-US", "MM/dd/yyyy", "h:mm tt"), "10/18/2026, 1:45 PM UTC");
  assert.equal(certificateDateTimeExample("en", "yyyy-MM-dd", "HH:mm"), "2026-10-18, 13:45 UTC");
});

test("certificate time setting presents both clock choices", () => {
  assert.deepEqual(certificateTimePatterns, [
    { value: "HH:mm", label: "24-hour" },
    { value: "h:mm tt", label: "12-hour" },
  ]);
});

test("certificate API schemas accept the DTO and refuse a missing required field", () => {
  assert.equal(fieldCatalogueEntrySchema.safeParse({ path: "unit.serial", type: "string", repeating: false, description: null }).success, true);
  assert.equal(fieldCatalogueEntrySchema.safeParse({ path: "unit.serial", type: "string", description: null }).success, false);
  const source = readFileSync(new URL("../src/certificates/api.ts", import.meta.url), "utf8");
  assert.doesNotMatch(source, /\bapi(?:<[^>]*>)?\(/);
});

test("certificate date samples use the tenant time zone", () => {
  const sample = certificateDateTimeExample("en", "d MMM yyyy", "HH:mm", "Asia/Kolkata");
  assert.equal(sample, "18 Oct 2026, 19:15 IST");
});

test("release settings validate BOM content for the default and each item-class override", () => {
  const settings = { defaultIssuePoint: "at_build_release", defaultBomContent: "full_bom",
    itemClassOverrides: [{ itemClass: "Compute", issuePoint: "on_request", bomContent: "serialised_only" }],
    version: "AAAAAAAAB9E=" };
  assert.equal(z.safeParse(releaseRecordIssuePointSettings, settings).success, true);
  assert.equal(z.safeParse(releaseRecordIssuePointSettings, { ...settings, defaultBomContent: "everything" }).success, false);
  const source = readFileSync(new URL("../src/settings/CertificateSettings.tsx", import.meta.url), "utf8");
  assert.match(source, /only serialised components can be printed/);
  assert.match(source, /BOM content/);
});
