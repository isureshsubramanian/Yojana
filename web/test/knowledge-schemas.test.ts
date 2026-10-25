import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import * as z from "zod/mini";
import { entryDraftRequest, questionThread, trainingWelcome, writtenAgainst } from "../src/questions/types.ts";
import { mailRecipientInput, sendCoverageMail, sentMailDetail } from "../src/mail/api.ts";

// ADR-0019 Knowledge batch: the schemas follow the C# views of the question, knowledge-entry, training and customer-mail
// endpoints. Values are demo-format, not real data.
const source = (path: string) => readFileSync(new URL(`../src/${path}`, import.meta.url), "utf8");
const id = (n: number) => `0199a1b2-0000-7000-8000-${String(n).padStart(12, "0")}`;

test("Knowledge and mail API files make no unchecked api calls", () => {
  for (const file of ["questions/api.ts", "mail/api.ts"]) {
    const text = source(file);
    assert.doesNotMatch(text, /\bapi(<[^>]*>)?\(/, file);
    assert.match(text, /apiParsed\(/, file);
  }
});

test("the training welcome card is optional, because the server answers 204 when there is none", () => {
  assert.equal(z.parse(z.optional(trainingWelcome), undefined), undefined);
  const card = { pathId: id(1), role: "service_desk", roleLabel: "Service desk", introduction: "Start here.", entries: 6, readEntries: 2 };
  assert.equal(z.parse(z.optional(trainingWelcome), card)?.readEntries, 2);
});

test("a question thread with no answer yet carries null acceptance and closure fields", () => {
  const thread = {
    questionId: id(2), recordKind: "unit", recordId: id(3), recordName: "SRV-R760 · CN7023X41", title: "PSU fault after firmware 2.4",
    body: "Unit reboots under load.", status: "open", directedToUserName: null, directedToRoleLabel: "Service desk",
    askedByName: "Priya Raman", askedAt: "2026-10-24T08:00:00Z", acceptedReplyId: null, closedReason: null, closedByName: null,
    closedAt: null, questionRevisionCount: 0, attachments: [], replies: [],
    allowedActions: { canEditQuestion: true, canReply: true, canAcceptAnswer: false, canCloseWithoutAnswer: true, canReopen: false, canTurnIntoEntry: false, entryId: null },
  };
  assert.equal(z.parse(questionThread, thread).acceptedReplyId, null);
  assert.equal(z.safeParse(questionThread, { ...thread, status: "pending" }).success, false);
});

test("an entry draft may send written-against references without the server-only resolved flag", () => {
  const reference = { kind: "screen", reference: "units.detail", label: "Unit detail" } as const;
  assert.equal(z.safeParse(writtenAgainst, reference).success, true);
  const draft = { title: "PSU fault", problem: "Reboots", answer: "Replace PSU", steps: "", tags: [{ kind: "role", value: "service_desk" }], recordLinks: [], writtenAgainst: [reference] };
  assert.equal(z.safeParse(entryDraftRequest, draft).success, true);
});

test("mail recipients may leave out name and confirmation, and the send command needs both recipient lists", () => {
  assert.equal(z.safeParse(mailRecipientInput, { address: "ops@nandira.example" }).success, true);
  const send = { commandId: id(4), templateVersionId: id(5), language: "en", to: [{ address: "ops@nandira.example" }], cc: [], note: null, signedBy: "me", copyToMeOnly: false };
  assert.equal(z.safeParse(sendCoverageMail, send).success, true);
  assert.equal(z.safeParse(sendCoverageMail, { ...send, signedBy: "someone" }).success, false);
  const row = {
    messageId: id(6), queuedAt: "2026-10-24T09:00:00Z", to: "ops@nandira.example", toName: null, ccCount: 0, customerName: "Nandira Systems",
    subject: "Your coverage", templateName: "Coverage summary", templateVersion: 2, language: "en", kind: "coverage", kindName: "Coverage summary",
    aboutLabel: "SRV-R760 · CN7023X41", unitId: id(3), sentBy: "Priya Raman", automatic: false, status: "sent", attempts: 1, nextAttemptAt: null, lastError: null,
  };
  const detail = { row, cc: [], html: "<p>Hi</p>", text: "Hi", attachmentName: null, hasNote: false, transport: null, sentAt: null, timeline: [] };
  assert.equal(z.parse(sentMailDetail, detail).row.status, "sent");
});
