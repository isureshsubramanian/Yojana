import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import type { MailLanguageContent } from "../src/mail/api.ts";
import {
  addLanguage, deliveryState, fieldsMentioned, insertField, itemToken, listOf, looksLikeAddress, moveBlock, newBlock, pieces, readText,
  recipientFor, templateState, writeText,
} from "../src/mail/format.ts";

const source = (path: string) => readFileSync(new URL(`../src/${path}`, import.meta.url), "utf8");

const fields = [
  { path: "recipient.name", label: "Contact name", group: "Recipient", repeats: false, itemOf: null, format: null },
  { path: "covers", label: "Each live cover", group: "Cover", repeats: true, itemOf: null, format: null },
  { path: "covers.family_name", label: "Cover", group: "Cover", repeats: false, itemOf: "covers", format: null },
  { path: "covers.until", label: "Until", group: "Cover", repeats: false, itemOf: "covers", format: "date" },
  { path: "links.unit_portal", label: "Portal link", group: "Message", repeats: false, itemOf: null, format: null },
];

const english: MailLanguageContent = {
  language: "en", subject: "Cover for {{unit.serial}}", preheader: null, bandLabel: "Warranty cover", heading: "What's covered",
  blocks: [
    { type: "paragraph", text: "Dear {{recipient.name}}," },
    { type: "heading", text: "Your cover" },
    { type: "table", each: "covers", emptyText: "None.", columns: [{ header: "Cover", text: "{{family_name}}" }] },
    { type: "button", label: "Open", link: "links.unit_portal" },
  ],
};

test("a field goes in at the caret, and inside a table's value as the item's own name", () => {
  assert.deepEqual(insertField("Dear ,", "recipient.name", 5, 5), { text: "Dear {{recipient.name}},", caret: 23 });
  assert.deepEqual(insertField("Dear X,", "recipient.name", 5, 6), { text: "Dear {{recipient.name}},", caret: 23 });
  assert.equal(itemToken(fields[2]!), "family_name");
  assert.equal(listOf(english, "b2.c0.text"), "covers");
  assert.equal(listOf(english, "b2.c0.header"), null);
  assert.equal(listOf(english, "b0.text"), null);
});

test("keyed texts read and write the right box without touching the others", () => {
  assert.equal(readText(english, "b2.c0.text"), "{{family_name}}");
  assert.equal(readText(english, "b3.label"), "Open");
  const changed = writeText(english, "b2.c0.header", "Plan");
  assert.equal(changed.blocks[2]!.columns![0]!.header, "Plan");
  assert.equal(changed.blocks[2]!.columns![0]!.text, "{{family_name}}");
  assert.equal(english.blocks[2]!.columns![0]!.header, "Cover");
  assert.equal(writeText(english, "preheader", "").preheader, null);
  assert.equal(writeText(english, "b9.text", "x"), english);
});

test("the block list shows fields and logic apart from the words", () => {
  assert.deepEqual(pieces("Hi {{recipient.name}}{{#if x}}!{{/if}}"), [
    { kind: "text", value: "Hi " }, { kind: "field", value: "recipient.name" }, { kind: "logic", value: "#if x" },
    { kind: "text", value: "!" }, { kind: "logic", value: "/if" },
  ]);
  const used = fieldsMentioned(english, fields);
  for (const path of ["recipient.name", "covers", "covers.family_name", "links.unit_portal", "unit.serial"]) assert.ok(used.has(path), path);
  assert.ok(!used.has("covers.until"));
});

test("new blocks start valid where they can, and moves stay inside the list", () => {
  const table = newBlock("table", fields);
  assert.equal(table.each, "covers");
  assert.deepEqual(table.columns, [{ header: "Cover", text: "{{family_name}}" }, { header: "Until", text: "{{until}}" }]);
  assert.ok(table.emptyText);
  assert.equal(newBlock("button", fields).link, "links.unit_portal");
  assert.deepEqual(moveBlock(["a", "b", "c"], 0, 1), ["b", "a", "c"]);
  assert.deepEqual(moveBlock(["a", "b"], 0, -1), ["a", "b"]);
});

test("a new language starts as a copy of the one on screen", () => {
  const both = addLanguage([english], "de", english);
  assert.equal(both.length, 2);
  assert.equal(both[1]!.language, "de");
  assert.equal(both[1]!.blocks.length, 4);
  assert.notEqual(both[1]!.blocks, english.blocks);
  assert.equal(addLanguage(both, "de", english).length, 2);
});

test("states read as words: template versions and delivery", () => {
  assert.deepEqual(templateState({ retired: false, publishedVersion: 3, draftVersion: 4, waitingForReview: true }), { tone: "ok", text: "v3 published" });
  assert.deepEqual(templateState({ retired: false, publishedVersion: null, draftVersion: 1, waitingForReview: true }), { tone: "info", text: "v1 waiting for review" });
  assert.deepEqual(templateState({ retired: true, publishedVersion: null, draftVersion: null, waitingForReview: false }), { tone: "neutral", text: "Retired" });
  assert.equal(deliveryState({ status: "sent", attempts: 1, lastError: null }).text, "Delivered");
  assert.equal(deliveryState({ status: "retrying", attempts: 2, lastError: "busy" }).text, "Retrying · attempt 2");
  assert.equal(deliveryState({ status: "failed", attempts: 1, lastError: "smtp 550 RecipientNotAccepted" }).text, "Refused address");
  assert.equal(deliveryState({ status: "failed", attempts: 8, lastError: "timeout" }).text, "Not delivered");
});

test("a typed address is confirmed unless it is one of the customer's contacts", () => {
  const contacts = [{ name: "Priya Raman", address: "p.raman@meridian.example" }];
  assert.deepEqual(recipientFor(" P.Raman@meridian.example ", contacts, false), { address: "p.raman@meridian.example", name: "Priya Raman" });
  assert.deepEqual(recipientFor("ops@meridian.example", contacts, true), { address: "ops@meridian.example", confirmed: true });
  assert.ok(looksLikeAddress("ops@meridian.example"));
  assert.ok(!looksLikeAddress("ops@meridian"));
  assert.ok(!looksLikeAddress("Ops <ops@meridian.example>"));
});

test("screens follow the approved decisions: no logo, sandboxed previews, admin publishes, PDF always attached", () => {
  const designer = source("mail/MailDesigner.tsx");
  const compose = source("mail/ComposeCoverageMail.tsx");
  const sent = source("mail/SentLog.tsx");
  for (const screen of [designer, compose, sent]) {
    assert.doesNotMatch(screen, /type="file"/);
    assert.doesNotMatch(screen, /dangerouslySetInnerHTML/);
    for (const frame of screen.match(/<iframe[^>]*>/g) ?? []) assert.match(frame, /sandbox=""/);
  }
  assert.match(designer, /template\.canPublish/);
  assert.match(designer, /Ask an administrator to publish/);
  assert.match(compose, /Always attached/);
  assert.doesNotMatch(compose, /attachPdf: false|removeAttachment/);
  assert.match(compose, /I have checked it is the right address/);
  const routes = source("routes.tsx");
  for (const path of ["mail/templates", "mail/templates/:id", "mail/sent", "units/:unitId/email"]) assert.ok(routes.includes(`"${path}"`), path);
  assert.match(source("units/UnitDetail.tsx"), /to=\{`\/units\/\$\{unitId\}\/email`\}>E-mail to customer…/);
});

test("the role lists mirror the server's mail policies", () => {
  const server = readFileSync(new URL("../../src/Platform/Yojana.Platform/PlatformSetup.cs", import.meta.url), "utf8");
  const format = source("mail/format.ts");
  for (const [name, list] of [["CustomerMailDesigner", "DESIGNER_ROLES"], ["CustomerMailSender", "SENDER_ROLES"]] as const) {
    const policy = new RegExp(`AddPolicy\\(YojanaPolicy\\.${name},[^;]*?RequireClaim\\(YojanaClaim\\.Role,([^)]*)\\)`, "s").exec(server);
    assert.ok(policy, name);
    const serverRoles = [...policy[1]!.matchAll(/YojanaRole\.(\w+)/g)].map((match) => match[1]!.replace(/[A-Z]/g, (c, i) => (i ? "_" : "") + c.toLowerCase())).sort();
    const clientRoles = [...new RegExp(`${list} = \\[([^\\]]*)\\]`).exec(format)![1]!.matchAll(/"(\w+)"/g)].map((match) => match[1]!).sort();
    assert.deepEqual(clientRoles, serverRoles, name);
  }
});
