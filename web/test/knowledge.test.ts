import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";

const base = readFileSync(new URL("../src/questions/KnowledgeBase.tsx", import.meta.url), "utf8");
const editor = readFileSync(new URL("../src/questions/EntryEditor.tsx", import.meta.url), "utf8");
const subnav = readFileSync(new URL("../src/questions/KnowledgeSubnav.tsx", import.meta.url), "utf8");
const api = readFileSync(new URL("../src/questions/api.ts", import.meta.url), "utf8");
const routes = readFileSync(new URL("../src/routes.tsx", import.meta.url), "utf8");
const styles = readFileSync(new URL("../src/questions/questions.css", import.meta.url), "utf8");
const settings = readFileSync(new URL("../src/settings/Settings.tsx", import.meta.url), "utf8");
const screenChanges = readFileSync(new URL("../src/settings/KnowledgeScreenChanges.tsx", import.meta.url), "utf8");
const training = readFileSync(new URL("../src/questions/TrainingPaths.tsx", import.meta.url), "utf8");
const welcome = readFileSync(new URL("../src/questions/TrainingWelcomeCard.tsx", import.meta.url), "utf8");
const overview = readFileSync(new URL("../src/pages/Overview.tsx", import.meta.url), "utf8");
const reports = readFileSync(new URL("../src/questions/KnowledgeReports.tsx", import.meta.url), "utf8");

test("Knowledge base searches its server-paged table as you type and browses by role and task", () => {
  assert.match(routes, /path: "knowledge".*KnowledgeBase/s);
  assert.match(routes, /path: "knowledge\/:entryId".*KnowledgeBase/s);
  assert.match(base, /Search this table as you type/);
  assert.match(base, /knowledgeApi\.browse\(\{ query, role, task \}, cursor/);
  assert.match(base, /<DataGrid/);
  assert.match(base, /Browse by role/);
  assert.match(base, /By task/);
  assert.match(base, /footerNote="Search and filters run on the server"/);
});

test("Knowledge entry page shows reviewed case text, tags, written-against data and permission-filtered links", () => {
  for (const heading of ["Problem", "Answer", "Steps", "Written against", "Linked records"])
    assert.ok(base.includes(heading), heading);
  assert.match(base, /entry\.recordLinks\.visible\.map/);
  assert.match(base, /entry\.recordLinks\.hiddenCount/);
  assert.match(base, /you can’t open/);
  assert.match(base, /caseDisclosureTier/);
  assert.match(base, /row\.needsReview/);
  assert.match(base, /May be out of date/);
  assert.match(base, /entry\.reviewReasons\.map/);
  assert.match(base, /It stays available while a reviewer checks it/);
});

test("Entry editor keeps aligned side-by-side controls, version history and reviewer decisions", () => {
  assert.match(routes, /path: "knowledge\/entries\/review".*EntryEditor/s);
  assert.match(routes, /path: "knowledge\/entries\/:entryId".*EntryEditor/s);
  assert.match(editor, /Version history/);
  assert.match(editor, /Changes since the last round/);
  assert.match(editor, /Save draft/);
  assert.match(editor, /Submit for review/);
  assert.match(editor, /Approve and publish/);
  assert.match(editor, /Send back/);
  assert.match(editor, /note\.trim\(\)/);
  assert.match(styles, /\.knowledge-form-grid .*align-items: start/);
  assert.match(styles, /\.card \.knowledge-form-grid \.field \+ \.field \{ margin-top: 0; \}/);
  assert.match(editor, /filter === "needs_review"/);
  assert.match(editor, />Needs review</);
  assert.match(editor, /entry\.reviewReasons\.map/);
  assert.match(editor, /allowedActions\.canConfirmStillCorrect/);
  assert.match(editor, /knowledgeApi\.stillCorrect/);
  assert.match(editor, />Still correct</);
});

test("Tenant settings let an administrator record a referenced screen change", () => {
  assert.match(settings, /<KnowledgeScreenChanges \/>/);
  assert.match(screenChanges, /knowledgeApi\.screens/);
  assert.match(screenChanges, /knowledgeApi\.markScreenChanged/);
  assert.match(screenChanges, /screen\.label/);
  assert.match(screenChanges, /lastChangedByName/);
  assert.match(screenChanges, /Mark screen changed/);
  assert.match(screenChanges, /recorded and audited/);
});

test("Approved sub-navigation makes Training paths and Reports live", () => {
  for (const label of ["My questions", "Knowledge base", "Entries to review", "Training paths", "Reports"])
    assert.ok(subnav.includes(label), label);
});

test("Knowledge reports follow the approved periods, filtering, metrics and privacy help", () => {
  assert.match(routes, /path: "knowledge\/reports".*KnowledgeReports/s);
  for (const label of ["30 days", "90 days", "12 months", "By directed role", "By record kind", "By topic", "Asked again despite an entry", "Unanswered by age", "Read by new team members", "Export CSV"])
    assert.ok(reports.includes(label), label);
  assert.match(reports, /reportsApi\.report\(filters/);
  assert.match(reports, /select\("directedRole"/);
  assert.match(reports, /select\("recordKind"/);
  assert.match(reports, /select\("topic"/);
  assert.match(reports, /PERCENTILE_CONT/);
  assert.match(reports, /tenant-local calendar dates/);
  assert.match(reports, /question text are blank/);
  assert.doesNotMatch(reports, /per-person question/i);
});

test("Training paths implement ordered reading, reviewer maintenance and team-safe progress", () => {
  assert.match(routes, /path: "knowledge\/training".*TrainingPaths/s);
  assert.match(training, /trainingApi\.save/);
  assert.match(training, /Move .* up/);
  assert.match(training, /Remove/);
  assert.match(training, /Mark as read/);
  assert.match(training, /item\.warning/);
  assert.match(training, /Questions asked in first 30 days · team total only/);
  assert.match(training, /no scores, rankings or per-person question counts/i);
  assert.doesNotMatch(training, /person\.questions/);
});

test("Overview shows the dismissible first-sign-in training card", () => {
  assert.match(overview, /<TrainingWelcomeCard \/>/);
  assert.match(welcome, /trainingApi\.welcome/);
  assert.match(welcome, /trainingApi\.dismissWelcome/);
  assert.match(welcome, /Start reading/);
  assert.match(welcome, /Questions → Training paths/);
});

test("Entry editor uses policy-version and screen pickers and marks unknown legacy references", () => {
  assert.match(editor, /knowledgeApi\.policyVersionChoices/);
  assert.match(editor, /knowledgeApi\.screenChoices/);
  assert.match(editor, /Search policy name or code/);
  assert.match(editor, /Search screens/);
  assert.match(editor, /Unknown reference/);
  assert.doesNotMatch(editor, /Stable reference|Readable label/);
});

test("Knowledge mutations are actor-free and use the entry endpoints", () => {
  for (const operation of ["browse", "entry", "review", "tagChoices", "changes", "save", "submit", "approve", "sendBack", "retire", "stillCorrect", "screens", "markScreenChanged", "policyVersionChoices", "screenChoices"])
    assert.match(api, new RegExp(`${operation}:`));
  assert.doesNotMatch(api, /actor|authorId|reviewerId|submittedBy/i);
});
