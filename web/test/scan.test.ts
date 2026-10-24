import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { AltKeypadCode, DEFAULT_SCANNING_POLICY, GROUP_SEPARATOR, PauseEndedScan, ScanRepeatSuppressor, ScanSettingsSessionCache, ScanTimer, displayScan, recoverGroupSeparators, separatorForControlKey, submission } from "../src/components/scan.ts";

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
  const s = submission("  2506-493445{GS}x ", "scan", "enter");
  assert.equal(s.value, `2506-493445${GROUP_SEPARATOR}x`);
  assert.equal(s.hasGroupSeparator, true);
  assert.equal(s.endedBy, "enter");
  assert.equal(displayScan(s.value), "2506-493445⟨GS⟩x");
});

class FakeTimeouts {
  private next = 1;
  private tasks = new Map<number, { at: number; callback: () => void }>();
  now = 0;

  set = (callback: () => void, delayMs: number): unknown => {
    const id = this.next++;
    this.tasks.set(id, { at: this.now + delayMs, callback });
    return id;
  };
  clear = (handle: unknown): void => { this.tasks.delete(handle as number); };
  advance(ms: number): void {
    this.now += ms;
    const due = [...this.tasks.entries()].filter(([, task]) => task.at <= this.now).sort((a, b) => a[1].at - b[1].at);
    for (const [id, task] of due) { this.tasks.delete(id); task.callback(); }
  }
}

function type(controller: PauseEndedScan, clock: FakeTimeouts, text: string, gap: number, submitted: ReturnType<typeof submission>[]): { value: string } {
  const input = { value: "" };
  for (const character of text) {
    clock.advance(gap); input.value += character;
    controller.key(clock.now, 300, () => input.value, scan => submitted.push(scan));
  }
  return input;
}

test("pause submits scanner-speed input but typed text never auto-submits", () => {
  const fastClock = new FakeTimeouts(); const fastSubmitted: ReturnType<typeof submission>[] = [];
  const fast = new PauseEndedScan(new ScanTimer(), fastClock);
  type(fast, fastClock, "3R29440J", 10, fastSubmitted);
  fastClock.advance(299); assert.equal(fastSubmitted.length, 0);
  fastClock.advance(1); assert.equal(fastSubmitted.length, 1);
  assert.deepEqual({ value: fastSubmitted[0]!.value, source: fastSubmitted[0]!.source, endedBy: fastSubmitted[0]!.endedBy },
    { value: "3R29440J", source: "scan", endedBy: "pause" });

  const slowClock = new FakeTimeouts(); const slowSubmitted: ReturnType<typeof submission>[] = [];
  const slow = new PauseEndedScan(new ScanTimer(), slowClock);
  type(slow, slowClock, "HXC4M5AFPEK3", 100, slowSubmitted);
  slowClock.advance(300); assert.equal(slowSubmitted.length, 0);
});

test("Enter stays immediate for scanner and typed input", () => {
  const clock = new FakeTimeouts(); const submitted: ReturnType<typeof submission>[] = [];
  const fast = new PauseEndedScan(new ScanTimer(), clock);
  const fastInput = type(fast, clock, "S802C1A1950E3132074", 10, submitted);
  const entered = fast.enter(fastInput.value);
  assert.equal(entered.source, "scan"); assert.equal(entered.endedBy, "enter");
  clock.advance(300); assert.equal(submitted.length, 0);

  const typed = new PauseEndedScan(new ScanTimer(), new FakeTimeouts());
  assert.equal(typed.enter("typed value").endedBy, "enter");
});

test("repeat suppression drops only the same value inside the window", () => {
  const repeats = new ScanRepeatSuppressor();
  assert.equal(repeats.accept("A8906101767472A", 100, 1500), true);
  assert.equal(repeats.accept("A8906101767472A", 1600, 1500), false);
  assert.equal(repeats.accept("0104043752177374", 1601, 1500), true);
  assert.equal(repeats.accept("A8906101767472A", 1602, 1500), true);
  assert.equal(repeats.accept("A8906101767472A", 3103, 1500), true);
});

test("a zero repeat window disables suppression", () => {
  const repeats = new ScanRepeatSuppressor();
  assert.equal(repeats.accept("same", 100, 0), true);
  assert.equal(repeats.accept("same", 101, 0), true);
});

test("scan settings are read once per session and failures use defaults", async () => {
  const cache = new ScanSettingsSessionCache(); let reads = 0;
  const read = async () => { reads += 1; return { endPauseMs: 250, repeatWindowMs: 900 }; };
  assert.deepEqual(await cache.load("user:tenant", read), { endPauseMs: 250, repeatWindowMs: 900 });
  assert.deepEqual(await cache.load("user:tenant", read), { endPauseMs: 250, repeatWindowMs: 900 });
  assert.equal(reads, 1);
  assert.deepEqual(await cache.load("user:other", async () => { throw new Error("offline"); }), DEFAULT_SCANNING_POLICY);
});

test("scanning settings and every resolving scan screen reject web addresses", () => {
  const source = (path: string) => readFileSync(new URL(`../src/${path}`, import.meta.url), "utf8");
  const page = source("settings/Scanning.tsx");
  for (const token of ["tenant_administrator", "End pause", 'min="150"', 'max="1000"', "Repeat window", 'min="0"', 'max="10000"'])
    assert.ok(page.includes(token), token);
  const screens = ["units/UnitSearch.tsx", "units/Receive.tsx", "returns/ReturnReceipt.tsx", "replacements/AwaitingReturns.tsx", "claims/ClaimIntake.tsx"];
  for (const screen of screens) {
    const text = source(screen);
    assert.ok(text.includes("isUrlScanFormat"), screen);
    assert.ok(text.includes("This is a web address, not a serial"), screen);
  }
});

// Replays key events through AltKeypadCode the way ScanInput applies them: consumed keys are dropped, inserted
// characters land at the end, other single-character keys are typed.
function typeKeys(alt: AltKeypadCode, events: readonly string[]): string {
  let field = "";
  for (const event of events) {
    if (event.startsWith("up:")) {
      field += alt.keyUp(event.slice(3)) ?? "";
      continue;
    }
    const step = alt.keyDown(event);
    field += step.insert ?? "";
    if (!step.consume && event.length === 1) field += event;
  }
  return field;
}

// The owner's probe of a scanner on factory settings (OD-88): Clear, Alt, 0, 0, 2, 9, Alt released, Clear.
const separatorKeys = ["Clear", "Alt", "0", "0", "2", "9", "up:Alt", "Clear"];

test("an Alt + number-pad separator becomes the group separator instead of the digits 0029", () => {
  const keys = [..."21HXC4M5AFPEK3", ...separatorKeys, ..."10LOT2510"];
  assert.equal(typeKeys(new AltKeypadCode(false), keys), `21HXC4M5AFPEK3${GROUP_SEPARATOR}10LOT2510`);
});

test("Alt sent twice before the digits still gives one separator", () => {
  const keys = ["2", "1", "A", "Clear", "Alt", "Alt", "0", "0", "2", "9", "up:Alt", "Clear", "1", "0", "B"];
  assert.equal(typeKeys(new AltKeypadCode(false), keys), `21A${GROUP_SEPARATOR}10B`);
});

test("a sequence ended by the next key rather than Alt released still gives the separator", () => {
  assert.equal(typeKeys(new AltKeypadCode(false), ["A", "Alt", "0", "0", "2", "9", "B"]), `A${GROUP_SEPARATOR}B`);
});

test("where the system composes Alt codes, digits pass through and only control characters are added", () => {
  const windows = new AltKeypadCode(true);
  const separatorStep = windows.keyDown("Alt");
  assert.equal(separatorStep.consume, true);
  for (const digit of "0029") assert.equal(windows.keyDown(digit).consume, false);
  assert.equal(windows.keyUp("Alt"), GROUP_SEPARATOR);
  windows.keyDown("Alt");
  for (const digit of "0065") windows.keyDown(digit);
  assert.equal(windows.keyUp("Alt"), null);
});

test("printable Alt codes are typed where the system does not compose them", () => {
  assert.equal(typeKeys(new AltKeypadCode(false), ["Alt", "0", "0", "6", "5", "up:Alt"]), "A");
});

test("ordinary typing, a bare Alt press and Clear add nothing", () => {
  assert.equal(typeKeys(new AltKeypadCode(false), ["Clear", "1", "2", "Alt", "up:Alt", "3"]), "123");
});

test("a separator typed twice, by the system and the scan box, is kept once", () => {
  assert.equal(recoverGroupSeparators(`21A${GROUP_SEPARATOR}${GROUP_SEPARATOR}10B`), `21A${GROUP_SEPARATOR}10B`);
});

test("model-number refusals offer a reasoned confirmation on every serial capture screen", () => {
  const source = (path: string) => readFileSync(new URL(`../src/${path}`, import.meta.url), "utf8");
  const confirmation = source("units/ModelNumberConfirmation.tsx");
  for (const token of ["serial_is_model_number", "This really is the serial", "Reason", "Confirm serial"])
    assert.ok(confirmation.includes(token), token);
  for (const screen of ["units/Receive.tsx", "units/SerialRanges.tsx", "units/UnitDetail.tsx", "returns/ReturnReceipt.tsx"]) {
    const text = source(screen);
    assert.ok(text.includes("isModelNumberRefusal"), screen);
    assert.ok(text.includes("ModelNumberConfirmation"), screen);
    assert.ok(text.includes("modelNumberConfirmationReason"), screen);
  }
  const settings = source("settings/ItemSerialisation.tsx");
  for (const token of ["tenant_administrator", "Item serialisation", "Model and part numbers", "20", "64", "ItemField"])
    assert.ok(settings.includes(token), token);
});
