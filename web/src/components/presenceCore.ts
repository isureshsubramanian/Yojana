import { createElement } from "react";

export type PresenceMode = "viewing" | "editing";
export interface PresenceUser { userId: string; mode: PresenceMode; since: string }

export interface PresenceClient {
  heartbeat(documentType: string, documentId: string, mode: PresenceMode): Promise<PresenceUser[]>;
  leave(documentType: string, documentId: string): Promise<void>;
}

export interface PresenceEnvironment {
  hidden(): boolean;
  every(milliseconds: number, callback: () => void): unknown;
  cancel(handle: unknown): void;
  onVisibility(callback: () => void): () => void;
  onFocus(callback: () => void): () => void;
  onPageHide(callback: () => void): () => void;
}

export interface PresenceHeartbeat { updateMode(mode: PresenceMode): void; stop(): void }

export function createPresenceHeartbeat({ documentType, documentId, initialMode, onUsers, client, environment }: {
  documentType: string; documentId: string; initialMode: PresenceMode;
  onUsers: (users: PresenceUser[]) => void; client: PresenceClient; environment: PresenceEnvironment;
}): PresenceHeartbeat {
  let mode = initialMode;
  let timer: unknown = null;
  let stopped = false;
  let pending = false;
  let refreshAfterPending = false;
  const heartbeat = () => {
    if (stopped || environment.hidden() || pending) return;
    pending = true;
    void client.heartbeat(documentType, documentId, mode)
      .then((users) => { if (!stopped) onUsers(users); })
      .catch(() => { /* presence is advisory; the document stays usable while offline */ })
      .finally(() => {
        pending = false;
        if (refreshAfterPending) { refreshAfterPending = false; heartbeat(); }
      });
  };
  const pause = () => { if (timer !== null) { environment.cancel(timer); timer = null; } };
  const resume = () => {
    if (stopped || environment.hidden()) return;
    heartbeat();
    if (timer === null) timer = environment.every(20_000, heartbeat);
  };
  const visibility = () => { if (environment.hidden()) pause(); else resume(); };
  const stop = () => {
    if (stopped) return;
    stopped = true; pause(); off.forEach((remove) => remove());
    void client.leave(documentType, documentId).catch(() => { /* expiry is the fallback for a dropped leave */ });
  };
  const off = [environment.onVisibility(visibility), environment.onFocus(resume), environment.onPageHide(stop)];
  resume();
  return {
    updateMode(next) {
      if (mode === next) return;
      mode = next;
      if (pending) refreshAfterPending = true; else heartbeat();
    },
    stop,
  };
}

function initials(name: string): string {
  const words = name.replace("…", "").trim().split(/\s+/).filter(Boolean);
  return words.slice(0, 2).map((word) => word[0]?.toUpperCase() ?? "").join("") || "?";
}

function time(value: string): string {
  return new Date(value).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" });
}

export function PresenceChipList({ users, person }: { users: readonly PresenceUser[]; person: (userId: string) => string }) {
  if (users.length === 0) return null;
  return createElement("div", { className: "presence", "aria-live": "polite" }, ...users.map((user) => {
    const name = person(user.userId);
    const label = user.mode === "editing" ? `${name} is editing` : `${name} viewing`;
    return createElement("span", {
      className: `presence-who${user.mode === "editing" ? " editing" : ""}`,
      title: `${user.mode === "editing" ? "Editing" : "Viewing"} since ${time(user.since)}`,
      key: user.userId,
    }, createElement("span", { className: "presence-avatar", "aria-hidden": "true" }, initials(name)),
    `${label} · since ${time(user.since)}`);
  }));
}
