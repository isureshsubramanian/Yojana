import { test } from "node:test";
import assert from "node:assert/strict";
import { GROUP_SEPARATOR, ScanTimer, displayScan, recoverGroupSeparators, separatorForControlKey, submission } from "../src/components/scan.ts";

test("Ctrl+] is the FNC1 group separator; other keys are not", () => {
  assert.equal(separatorForControlKey("]", true), GROUP_SEPARATOR);
  assert.equal(separatorForControlKey("]", false), null);
  assert.equal(separatorForControlKey("a", true), null);
});

test("literal separator tokens from wedge configurations are recovered", () => {
  assert.equal(recoverGroupSeparators("(01)09506000134352(21)2506493445{GS}(10)L2503"), `(01)09506000134352(21)2506493445${GROUP_SEPARATOR}(10)L2503`);
  assert.equal(recoverGroupSeparators("a<GS>b\\x1dc"), `a${GROUP_SEPARATOR}b${GROUP_SEPARATOR}c`);
});

test("a burst of fast keystrokes classifies as a scan; typing does not", () => {
  const fast = new ScanTimer();
  let t = 0;
  for (let i = 0; i < 12; i++) fast.key((t += 10));
  assert.equal(fast.classify(12), "scan");
  const slow = new ScanTimer();
  t = 0;
  for (let i = 0; i < 12; i++) slow.key((t += 180));
  assert.equal(slow.classify(12), "typed");
});

test("a submission trims, recovers separators and reports them", () => {
  const s = submission("  2506-493445{GS}x ", "scan");
  assert.equal(s.value, `2506-493445${GROUP_SEPARATOR}x`);
  assert.equal(s.hasGroupSeparator, true);
  assert.equal(displayScan(s.value), "2506-493445⟨GS⟩x");
});
