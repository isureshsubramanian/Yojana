import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";

const editor = readFileSync(new URL("../src/corrections/WorkbenchChoices.tsx", import.meta.url), "utf8");

test("workbench choice roles and reason are picked from the command's own lists, never typed", () => {
  assert.doesNotMatch(editor, /id="choice-roles"/);
  assert.doesNotMatch(editor, /<input id="choice-reason"/);
  assert.match(editor, /<select id="choice-reason"/);
  assert.match(editor, /workbenchApi\.reasons\(command/);
  assert.match(editor, /COMMANDS\[command\]\?\.commitRoles/);
  assert.match(editor, /type="checkbox" checked=\{commitRoles\.includes\(role\)\}/);
  assert.match(editor, /Choose at least one role\./);
});
