import { createElement, Fragment, useEffect, useRef, useState } from "react";
import * as z from "zod/mini";
import { apiParsed } from "../api/client";
import { usePeople } from "../people/people";
import { createPresenceHeartbeat, PresenceChipList } from "./presenceCore";
import type { PresenceClient, PresenceEnvironment, PresenceHeartbeat, PresenceMode, PresenceUser } from "./presenceCore";

export type { PresenceMode, PresenceUser } from "./presenceCore";
export { PresenceChipList, createPresenceHeartbeat } from "./presenceCore";

// The annotation keeps the schema and presenceCore's PresenceUser in step.
const presenceUser: z.ZodMiniType<PresenceUser> = z.object({ userId: z.string(), mode: z.enum(["viewing", "editing"]), since: z.string() });
const presenceResponse = z.object({ users: z.array(presenceUser) });

function listen(target: Document | Window, event: string, callback: () => void): () => void {
  target.addEventListener(event, callback);
  return () => target.removeEventListener(event, callback);
}

const browserEnvironment: PresenceEnvironment = {
  hidden: () => document.hidden,
  every: (milliseconds, callback) => window.setInterval(callback, milliseconds),
  cancel: (handle) => window.clearInterval(handle as number),
  onVisibility: (callback) => listen(document, "visibilitychange", callback),
  onFocus: (callback) => listen(window, "focus", callback),
  onPageHide: (callback) => listen(window, "pagehide", callback),
};

const presenceClient: PresenceClient = {
  async heartbeat(documentType, documentId, mode) {
    return (await apiParsed("/api/presence/heartbeat", presenceResponse, {
      method: "POST", body: { documentType, documentId, mode },
    })).users;
  },
  async leave(documentType, documentId) {
    await fetch("/api/presence", {
      method: "DELETE", credentials: "same-origin", keepalive: true,
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ documentType, documentId }),
    });
  },
};

export function useDocumentPresence(documentType: string, documentId: string, editing: boolean): PresenceUser[] {
  const [users, setUsers] = useState<PresenceUser[]>([]);
  const heartbeat = useRef<PresenceHeartbeat | null>(null);
  const mode: PresenceMode = editing ? "editing" : "viewing";
  useEffect(() => {
    if (!documentType || !documentId) { setUsers([]); return; }
    setUsers([]);
    const next = createPresenceHeartbeat({
      documentType, documentId, initialMode: mode, onUsers: setUsers,
      client: presenceClient, environment: browserEnvironment,
    });
    heartbeat.current = next;
    return () => { heartbeat.current = null; next.stop(); };
    // Mode updates separately so a dirty transition does not send a leave or reset the presence list.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [documentType, documentId]);
  useEffect(() => { heartbeat.current?.updateMode(mode); }, [mode]);
  return users;
}

export function PresenceChips({ users }: { users: readonly PresenceUser[] }) {
  const people = usePeople(users.map((user) => user.userId));
  return createElement(PresenceChipList, { users, person: (userId: string) => resolvedPerson(userId, people(userId)) });
}

export function PresenceAdvisory({ users, editing, documentLabel }: {
  users: readonly PresenceUser[]; editing: boolean; documentLabel: string;
}) {
  const editors = users.filter((user) => user.mode === "editing");
  const people = usePeople(editors.map((user) => user.userId));
  if (editors.length === 0) return null;
  const names = editors.map((user) => resolvedPerson(user.userId, people(user.userId)));
  const subject = names.length === 1 ? `${names[0]} is` : `${names.join(", ")} are`;
  return createElement("div", { className: `note warn presence-warning${editing ? " editing" : ""}` },
    createElement("strong", null, `${subject} editing this ${documentLabel}.`),
    createElement(Fragment, null, " You can keep working; if they save first, your save is refused and nothing of theirs is lost. Presence is a hint, never a lock."));
}

function resolvedPerson(userId: string, label: string): string {
  return label === `${userId.slice(0, 8)}…` ? "Someone" : label;
}
