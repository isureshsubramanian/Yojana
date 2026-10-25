import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import * as z from "zod/mini";
import { session } from "../src/api/types.ts";
import { importFileSettingsApi } from "../src/settings/importFileSettingsApi.ts";
import { passwordResetOptions } from "../src/settings/passwordResetApi.ts";
import { erasureConfirmInput } from "../src/settings/privacyApi.ts";

// ADR-0019 Settings batch: the schemas follow the C# views of the settings, security and privacy endpoints.
// Values are demo-format, not real data.
const source = (path: string) => readFileSync(new URL(`../src/${path}`, import.meta.url), "utf8");

test("Settings API files make no unchecked api calls", () => {
  for (const file of ["certificateSettingsApi.ts", "companyApi.ts", "importFileSettingsApi.ts", "itemSerialisationApi.ts", "numberingApi.ts",
    "passwordResetApi.ts", "privacyApi.ts", "scanningApi.ts", "securityEventsApi.ts", "settingsSummaryApi.ts", "timeZoneSettingsApi.ts", "userManagementApi.ts"]) {
    const text = source(`settings/${file}`);
    assert.doesNotMatch(text, /\bapi(<[^>]*>)?\(/, file);
    assert.match(text, /apiParsed\(/, file);
  }
});

test("the import-file settings save sends the settings as one JSON object, not a JSON string encoded twice", async () => {
  const sent: string[] = [];
  const original = globalThis.fetch;
  const settings = { storage: "file" as const, archiveStore: "Archive A", archiveAge: 12, archiveAgeUnit: "months" as const, archiveStores: [{ name: "Archive A", kind: "folder" as const }], version: "AAAAAAAAB9E=" };
  globalThis.fetch = (async (_input: string | URL | Request, init?: RequestInit) => {
    sent.push(String(init?.body));
    return new Response(JSON.stringify(settings), { status: 200 });
  }) as typeof fetch;
  try {
    await importFileSettingsApi.save(settings);
    assert.deepEqual(JSON.parse(sent[0] ?? "null"), { storage: "file", archiveStore: "Archive A", archiveAge: 12, archiveAgeUnit: "months", expectedVersion: "AAAAAAAAB9E=" });
  } finally { globalThis.fetch = original; }
});

test("a session without a chosen tenant parses, and the recovery-code warning is optional", () => {
  const value = { user: { id: "0199a1b2-0000-7000-8000-000000000001", email: "priya.raman@dev.yojana.local", displayName: "Priya Raman" },
    tenant: null, roles: [], tenants: [{ code: "demo", name: "Demo IT Hardware", roles: ["service_desk"] }], passwordChangeRequired: false };
  assert.equal(z.parse(session, value).tenant, null);
  assert.equal(z.safeParse(session, { ...value, tenant: { code: "demo", name: "Demo IT Hardware" }, recoveryCodesLow: true }).success, true);
});

test("a temporary-password reason is one of the three the server sends, or null", () => {
  const options = { canSendLink: true, canSetTemporary: false, temporaryUnavailableReason: "tenant_setting_off" };
  assert.equal(z.safeParse(passwordResetOptions, options).success, true);
  assert.equal(z.safeParse(passwordResetOptions, { ...options, temporaryUnavailableReason: null }).success, true);
  assert.equal(z.safeParse(passwordResetOptions, { ...options, temporaryUnavailableReason: "unknown" }).success, false);
});

test("an erasure confirmation is checked with its typed confirmation and review decisions", () => {
  const request = { subjectKind: "Party", subjectId: "0199a1b2-0000-7000-8000-000000000002", requestReference: "DSR-2026-014", requestReceivedOn: "2026-10-20",
    reason: "Customer request", subjectIsNaturalPerson: true, confirmation: "ERASE", reviewDecisions: [{ itemKey: "claims:note:1", action: "Redact" }] };
  assert.equal(z.safeParse(erasureConfirmInput, request).success, true);
  assert.equal(z.safeParse(erasureConfirmInput, { ...request, reviewDecisions: [{ itemKey: "claims:note:1", action: "Delete" }] }).success, false);
});
