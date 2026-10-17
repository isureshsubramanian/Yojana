import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { navigation } from "../src/shell/navigation.ts";

test("every navigation entry has its own icon", () => {
  const seen = new Map<string, string>();
  for (const entry of navigation) {
    assert.ok(!seen.has(entry.icon), `${entry.label} reuses the ${entry.icon} icon of ${seen.get(entry.icon)}`);
    seen.set(entry.icon, entry.label);
  }
});

test("every navigation icon is drawn in the sprite", () => {
  const sprite = readFileSync(new URL("../src/shell/Icons.tsx", import.meta.url), "utf8");
  for (const entry of navigation) assert.ok(sprite.includes(`id="i-${entry.icon}"`), `${entry.label}: no i-${entry.icon} symbol`);
});
