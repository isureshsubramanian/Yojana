import assert from "node:assert/strict";
import { mock, test } from "node:test";
import { pollNotifications } from "../src/shell/notificationSource.ts";

const settle = async () => { await Promise.resolve(); await Promise.resolve(); };

test("question bell polling runs every 20 seconds and pauses while hidden", async () => {
  mock.timers.enable({ apis: ["setInterval"] });
  let hidden = false; let loads = 0; const listeners = new Map<string, () => void>();
  const subscribe = (key: string, callback: () => void) => { listeners.set(key, callback); return () => { listeners.delete(key); }; };
  const environment = {
    hidden: () => hidden,
    every: (milliseconds: number, callback: () => void) => setInterval(callback, milliseconds),
    cancel: (handle: unknown) => clearInterval(handle as ReturnType<typeof setInterval>),
    onVisibility: (callback: () => void) => subscribe("visibility", callback),
    onFocus: (callback: () => void) => subscribe("focus", callback),
  };
  try {
    const stop = pollNotifications(async () => { loads += 1; }, environment);
    await settle(); assert.equal(loads, 1);
    mock.timers.tick(20_000); await settle(); assert.equal(loads, 2);
    hidden = true; listeners.get("visibility")?.(); mock.timers.tick(60_000); await settle(); assert.equal(loads, 2);
    hidden = false; listeners.get("visibility")?.(); await settle(); assert.equal(loads, 3);
    listeners.get("focus")?.(); await settle(); assert.equal(loads, 4);
    stop(); mock.timers.tick(20_000); await settle(); assert.equal(loads, 4);
  } finally { mock.timers.reset(); }
});
