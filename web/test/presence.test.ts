import assert from "node:assert/strict";
import { mock, test } from "node:test";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { createPresenceHeartbeat, PresenceChipList, type PresenceClient, type PresenceUser } from "../src/components/presenceCore.ts";

const settle = async () => { await Promise.resolve(); await Promise.resolve(); };

test("presence heartbeat runs every 20 seconds and pauses while the page is hidden", async () => {
  mock.timers.enable({ apis: ["setInterval"] });
  let hidden = false;
  let heartbeats = 0;
  let leaves = 0;
  const listeners = new Map<string, () => void>();
  const client: PresenceClient = {
    async heartbeat() { heartbeats += 1; return []; },
    async leave() { leaves += 1; },
  };
  const environment = {
    hidden: () => hidden,
    every: (milliseconds: number, callback: () => void) => setInterval(callback, milliseconds),
    cancel: (handle: unknown) => clearInterval(handle as ReturnType<typeof setInterval>),
    onVisibility: (callback: () => void) => subscribe("visibility", callback),
    onFocus: (callback: () => void) => subscribe("focus", callback),
    onPageHide: (callback: () => void) => subscribe("pagehide", callback),
  };
  function subscribe(key: string, callback: () => void): () => void {
    listeners.set(key, callback); return () => { listeners.delete(key); };
  }

  try {
    const heartbeat = createPresenceHeartbeat({ documentType: "rma", documentId: "one", initialMode: "viewing", onUsers: () => {}, client, environment });
    await settle();
    assert.equal(heartbeats, 1);
    mock.timers.tick(20_000); await settle();
    assert.equal(heartbeats, 2);

    hidden = true; listeners.get("visibility")?.();
    mock.timers.tick(60_000); await settle();
    assert.equal(heartbeats, 2);
    hidden = false; listeners.get("visibility")?.(); await settle();
    assert.equal(heartbeats, 3, "becoming visible resumes with an immediate heartbeat");
    mock.timers.tick(20_000); await settle();
    assert.equal(heartbeats, 4);
    listeners.get("focus")?.(); await settle();
    assert.equal(heartbeats, 5, "focus refreshes presence immediately");

    heartbeat.stop(); await settle();
    assert.equal(leaves, 1);
  } finally {
    mock.timers.reset();
  }
});

test("presence chips render names, modes, start times and editing emphasis", () => {
  const users: PresenceUser[] = [
    { userId: "one", mode: "editing", since: "2026-09-01T10:42:00Z" },
    { userId: "two", mode: "viewing", since: "2026-09-01T10:39:00Z" },
  ];
  const names: Record<string, string> = { one: "S. Mehta", two: "J. Ruiz" };

  const html = renderToStaticMarkup(createElement(PresenceChipList, { users, person: (id) => names[id] ?? id }));

  assert.match(html, /presence-who editing/);
  assert.match(html, /S\. Mehta is editing/);
  assert.match(html, /J\. Ruiz viewing/);
  assert.match(html, /since [^<]+/);
});
