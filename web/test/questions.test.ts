import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import { humanise, initials, recordPath, statusLabel } from "../src/questions/model.ts";

const panel = readFileSync(new URL("../src/questions/QuestionsPanel.tsx", import.meta.url), "utf8");
const api = readFileSync(new URL("../src/questions/api.ts", import.meta.url), "utf8");
const styles = readFileSync(new URL("../src/questions/questions.css", import.meta.url), "utf8");
const tabs = readFileSync(new URL("../src/questions/RecordQuestionsTabs.tsx", import.meta.url), "utf8");
const mine = readFileSync(new URL("../src/questions/MyQuestions.tsx", import.meta.url), "utf8");
const routes = readFileSync(new URL("../src/routes.tsx", import.meta.url), "utf8");
const navigation = readFileSync(new URL("../src/shell/navigation.ts", import.meta.url), "utf8");
const topBar = readFileSync(new URL("../src/shell/TopBar.tsx", import.meta.url), "utf8");
const frame = readFileSync(new URL("../src/shell/Frame.tsx", import.meta.url), "utf8");
const preferences = readFileSync(new URL("../src/questions/QuestionNotificationPreferences.tsx", import.meta.url), "utf8");
const account = readFileSync(new URL("../src/pages/AccountSecurity.tsx", import.meta.url), "utf8");
const notificationPoll = readFileSync(new URL("../src/shell/notificationSource.ts", import.meta.url), "utf8");

test("the reusable record panel receives only the record contract and mounts on all six detail screens", () => {
  assert.match(panel, /QuestionsPanel\(\{ recordKind, recordId \}: \{ recordKind: string; recordId: string \}\)/);
  const screens = [
    ["units/UnitDetail.tsx", "unit"], ["claims/ClaimAdjudication.tsx", "claim"],
    ["returns/RmaDetail.tsx", "rma"], ["refurbishments/Refurbishment.tsx", "refurbishment_work_order"],
    ["entitlements/PolicyEditor.tsx", "warranty_policy"], ["certificates/CertificateViewer.tsx", "certificate"],
  ] as const;
  for (const [file, kind] of screens) {
    const source = readFileSync(new URL(`../src/${file}`, import.meta.url), "utf8");
    assert.match(source, new RegExp(`<QuestionsPanel recordKind="${kind}" recordId=`), file);
  }
  assert.match(tabs, /page\.counts\.open/);
  assert.match(tabs, /Questions <span className="muted">/);
});

test("thread controls are rendered only from server-provided permissions and history uses the drawer", () => {
  for (const permission of ["canEditQuestion", "canReply", "canAcceptAnswer", "canCloseWithoutAnswer", "canReopen"])
    assert.match(panel, new RegExp(`allowedActions\\.${permission}`));
  assert.match(panel, /item\.canEdit/);
  assert.match(panel, /openDrawer\(\{ eyebrow: reply \? "Reply history" : "Question history"/);
  assert.match(panel, /reason\.trim\(\)\.length <= 500/);
  for (const operation of ["reply", "editQuestion", "editReply", "accept", "close", "reopen", "revisions"])
    assert.match(api, new RegExp(`${operation}:`));
});

test("the ask box uses active person-or-role choices, the approved layout, and live similar answers", () => {
  assert.match(panel, /questionsApi\.addressees/);
  assert.match(panel, /<optgroup label="Roles">/);
  assert.match(panel, /<optgroup label="People">/);
  assert.match(panel, /questionsApi\.search\(title\.trim\(\), true/);
  assert.match(panel, /reviewedEntries\.length > 0/);
  assert.match(panel, /Already answered: read these before you ask/);
  assert.match(styles, /grid-template-columns: minmax\(0, 1fr\) 320px/);
  assert.match(styles, /\.card \.question-askbox \.row \.field \+ \.field \{ margin-top: 0; \}/);
  assert.doesNotMatch(api, /actor|askedBy|requestedBy/i);
});

test("My questions keeps search and chips on its server-paged table and summaries as ranked lists", () => {
  assert.match(routes, /path: "questions".*MyQuestions/s);
  assert.match(navigation, /path: "\/questions", label: "Questions"/);
  assert.match(mine, /Search this table as you type/);
  assert.match(mine, /questionsApi\.mine\(filters, cursor/);
  assert.match(mine, /<DataGrid/);
  for (const label of ["Asked by me", "Directed at me", "At my roles", "Status", "Record kind", "Age"])
    assert.ok(mine.includes(label), label);
  assert.equal((mine.match(/className=\{`chip-select/g) ?? []).length, 3);
  assert.match(mine, /title="Waiting by age"/);
  assert.match(mine, /title="Waiting by record kind"/);
  assert.match(mine, /<ul className="question-ranked">/);
  assert.doesNotMatch(mine, /Knowledge base|Team inbox|Unanswered/);
});

test("question display helpers keep record labels, status and routes readable", () => {
  assert.equal(humanise("refurbishment_work_order"), "Refurbishment work order");
  assert.equal(initials("Anika Iyer"), "AI");
  assert.equal(statusLabel("open", 1), "Open · 1 reply");
  assert.equal(statusLabel("answered"), "Answered");
  assert.equal(recordPath({ recordKind: "claim", recordId: "internal" }), "/claims/internal");
});

test("the shell bell consumes the narrow question source and polls with the presence lifecycle", () => {
  assert.match(frame, /notificationSources=\{notificationSources\}/);
  assert.match(topBar, /pollNotifications/);
  assert.match(notificationPoll, /20_000/);
  assert.match(topBar, /visibilitychange/);
  assert.match(topBar, /window\.addEventListener\("focus"/);
  assert.match(topBar, /summary\.count/);
  assert.match(topBar, /item\.href/);
  assert.match(api, /notifications\/bell\?take=5/);
});

test("account settings expose question e-mail defaults and the three person overrides", () => {
  assert.match(account, /<QuestionNotificationPreferences \/>/);
  assert.match(preferences, /Question notifications/);
  assert.match(preferences, /Use company defaults/);
  for (const value of ["immediately", "daily_digest", "off"])
    assert.match(preferences, new RegExp(`value="${value}"`));
  assert.match(preferences, /personalDefault/);
  assert.match(preferences, /roleDefault/);
  assert.match(api, /notifications\/preference/);
});
