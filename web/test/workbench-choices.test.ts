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

test("every command a choice can run has a readable name, including hand-offs and other entitlement commands", () => {
  const rules = readFileSync(new URL("../../src/Serialisation/Yojana.Serialisation/Workbench/WorkbenchChoiceRules.cs", import.meta.url), "utf8");
  const contracts = readFileSync(new URL("../../src/Serialisation/Yojana.Serialisation.Contracts/Workbench.cs", import.meta.url), "utf8");
  const literal = [...rules.matchAll(/"(entitlement\.[a-z_]+)"/g)].map((match) => match[1]);
  const handoff = [...contracts.matchAll(/(?:ShipWarrantyReplacement|ReleaseRefurbishment) = "([a-z_.]+)"/g)].map((match) => match[1]);
  assert.ok(literal.length >= 5 && handoff.length === 2);
  for (const command of [...literal, ...handoff]) assert.match(editor, new RegExp(`"${command.replace(".", "\\.")}": (?:\\{ label: )?"[A-Z]`), command);
  assert.match(editor, /row\.commands\.map\(commandLabel\)/);
});
