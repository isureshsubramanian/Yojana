import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";

const base = readFileSync(new URL("../src/questions/KnowledgeBase.tsx", import.meta.url), "utf8");
const editor = readFileSync(new URL("../src/questions/EntryEditor.tsx", import.meta.url), "utf8");
const subnav = readFileSync(new URL("../src/questions/KnowledgeSubnav.tsx", import.meta.url), "utf8");
const api = readFileSync(new URL("../src/questions/api.ts", import.meta.url), "utf8");
const routes = readFileSync(new URL("../src/routes.tsx", import.meta.url), "utf8");
const styles = readFileSync(new URL("../src/questions/questions.css", import.meta.url), "utf8");

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
  assert.doesNotMatch(base, /Needs review|may be out of date/i);
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
  assert.doesNotMatch(editor, /Needs review|may be out of date/i);
});

test("Only the approved live sub-navigation items are visible", () => {
  for (const label of ["My questions", "Knowledge base", "Entries to review"])
    assert.ok(subnav.includes(label), label);
  for (const hidden of ["Training paths", "Reports"])
    assert.doesNotMatch(subnav, new RegExp(hidden));
});

test("Knowledge mutations are actor-free and use the entry endpoints", () => {
  for (const operation of ["browse", "entry", "review", "tagChoices", "changes", "save", "submit", "approve", "sendBack", "retire"])
    assert.match(api, new RegExp(`${operation}:`));
  assert.doesNotMatch(api, /actor|authorId|reviewerId|submittedBy/i);
});
