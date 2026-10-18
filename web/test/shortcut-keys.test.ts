import assert from "node:assert/strict";
import { readdirSync, readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import test from "node:test";
import { matchesShortcut, runAfterPageShortcuts, shortcutPlatform, shortcutPresentation } from "../src/components/shortcutKeys.ts";

test("shortcut platform detection covers Windows, Linux and Apple user agents", () => {
  assert.equal(shortcutPlatform("Win32", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)"), "other");
  assert.equal(shortcutPlatform("Linux x86_64", "Mozilla/5.0 (X11; Linux x86_64)"), "other");
  assert.equal(shortcutPlatform("MacIntel", "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7)"), "apple");
  assert.equal(shortcutPlatform(undefined, "Mozilla/5.0 (iPad; CPU OS 18_0 like Mac OS X)"), "apple");
});

test("shortcut labels and spoken names match the detected platform", () => {
  assert.deepEqual(shortcutPresentation("mod+k", "apple"), { keys: ["⌘", "K"], ariaLabel: "Command+K" });
  assert.deepEqual(shortcutPresentation("mod+k", "other"), { keys: ["Ctrl", "K"], ariaLabel: "Control+K" });
  assert.deepEqual(shortcutPresentation("mod+enter", "other"), { keys: ["Ctrl", "Enter"], ariaLabel: "Control+Enter" });
  assert.deepEqual(shortcutPresentation("enter", "apple"), { keys: ["↵"], ariaLabel: "Enter" });
});

test("shortcut matching accepts either existing modifier key and rejects extra modifiers", () => {
  const event = (key: string, metaKey = false, ctrlKey = false, altKey = false, shiftKey = false) =>
    ({ key, metaKey, ctrlKey, altKey, shiftKey });
  assert.equal(matchesShortcut(event("Enter", true), "mod+enter"), true);
  assert.equal(matchesShortcut(event("Enter", false, true), "mod+enter"), true);
  assert.equal(matchesShortcut(event("Enter", false, true, false, true), "mod+enter"), false);
  assert.equal(matchesShortcut(event("k"), "mod+k"), false);
});

test("page single-key shortcuts can claim an event before the shell fallback", async () => {
  const routes: string[] = [];
  const claimed = { defaultPrevented: false };
  runAfterPageShortcuts(claimed, () => routes.push("corrections"));
  claimed.defaultPrevented = true;
  await Promise.resolve();
  assert.deepEqual(routes, []);

  runAfterPageShortcuts({ defaultPrevented: false }, () => routes.push("corrections"));
  await Promise.resolve();
  assert.deepEqual(routes, ["corrections"]);
});

test("shortcut labels are centralised and every Mod+Enter hint has the shared handler", () => {
  const root = fileURLToPath(new URL("../src", import.meta.url));
  const files = sourceFiles(root);
  const shared = fileURLToPath(new URL("../src/components/shortcutKeys.ts", import.meta.url));
  const receive = fileURLToPath(new URL("../src/units/Receive.tsx", import.meta.url));

  for (const file of files) {
    const source = readFileSync(file, "utf8");
    if (file !== shared) assert.doesNotMatch(source, /[⌘⌥⇧↵]/u, file);
    if (source.includes('<Shortcut keys="mod+enter"')) assert.match(source, /useModEnter\(/, file);
    if (file !== receive) assert.doesNotMatch(source, /⏎/u, file);
  }
  assert.match(readFileSync(receive, "utf8"), /Group-separator marker[\s\S]*⏎/u);
});

function sourceFiles(directory: string): string[] {
  return readdirSync(directory, { withFileTypes: true }).flatMap((entry) => {
    const path = `${directory}/${entry.name}`;
    return entry.isDirectory() ? sourceFiles(path) : /\.(?:css|ts|tsx)$/.test(entry.name) ? [path] : [];
  });
}
