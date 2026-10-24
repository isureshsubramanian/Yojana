import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import { filterNotices, licenceCounts, parseNotices } from "../src/pages/noticesModel.ts";

const shipped = readFileSync(new URL("../../src/Web/Yojana.Web/Notices/THIRD-PARTY-NOTICES.txt", import.meta.url), "utf8");

test("the shipped notices file splits into named components, each with its licence text", () => {
  const doc = parseNotices(shipped);
  assert.match(doc.preamble, /^THIRD-PARTY NOTICES FOR YOJANA/);
  const contents = (/CONTENTS\n\n([\s\S]*?)\n\n=/.exec(shipped)?.[1] ?? "").split("\n").filter((line) => line.startsWith("  - "));
  assert.equal(doc.components.length, contents.length);
  const core = doc.components.find((row) => row.name.startsWith("ASP.NET Core"));
  assert.equal(core?.licence, "MIT");
  assert.match(core?.copyright ?? "", /\.NET Foundation/);
  assert.match(core?.text ?? "", /Permission is hereby granted/);
  assert.ok(doc.components.every((row) => row.name && row.text), "every component has a name and text");
  assert.ok(doc.components.filter((row) => !row.licence).length === 0, "every component names its licence");
});

test("find matches name, licence or copyright; licences are counted", () => {
  const doc = parseNotices(shipped);
  assert.ok(filterNotices(doc.components, "react").some((row) => row.name.startsWith("react ")));
  assert.ok(filterNotices(doc.components, "apache-2.0").every((row) => /apache-2\.0/i.test(`${row.name} ${row.licence} ${row.copyright}`)));
  const counts = licenceCounts(doc.components);
  assert.equal(counts.reduce((sum, [, n]) => sum + n, 0), doc.components.length);
});

test("long licence values get a short list label", async () => {
  const { shortLicence } = await import("../src/pages/noticesModel.ts");
  assert.equal(shortLicence("MIT"), "MIT");
  assert.equal(shortLicence("see licence text below (file LICENSE.txt in the package)"), "In text");
  assert.equal(shortLicence("LicenseRef-Chromium-ThirdParty"), "LicenseRef-Chromi…");
});
