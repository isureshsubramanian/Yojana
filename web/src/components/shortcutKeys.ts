export type ShortcutPlatform = "apple" | "other";

export interface ShortcutKeyEvent {
  key: string;
  metaKey: boolean;
  ctrlKey: boolean;
  altKey: boolean;
  shiftKey: boolean;
}

export interface ShortcutPresentation {
  keys: string[];
  ariaLabel: string;
}

const APPLE_PLATFORMS = /mac|iphone|ipad|ipod/i;

export function shortcutPlatform(platform: string | undefined, userAgent: string | undefined): ShortcutPlatform {
  return APPLE_PLATFORMS.test(platform ?? "") || APPLE_PLATFORMS.test(userAgent ?? "") ? "apple" : "other";
}

function browserPlatform(): ShortcutPlatform {
  if (typeof navigator === "undefined") return "other";
  const userAgentData = navigator as Navigator & { userAgentData?: { platform?: string } };
  return shortcutPlatform(userAgentData.userAgentData?.platform || navigator.platform || undefined, navigator.userAgent);
}

// Platform detection is intentionally performed once. Every label and matcher reads the same decision.
export const currentShortcutPlatform = browserPlatform();

export function shortcutPresentation(shortcut: string, platform: ShortcutPlatform = currentShortcutPlatform): ShortcutPresentation {
  const tokens = shortcut.toLowerCase().split("+").filter(Boolean);
  const keys = tokens.map((token) => {
    if (token === "mod") return platform === "apple" ? "⌘" : "Ctrl";
    if (token === "enter") return platform === "apple" ? "↵" : "Enter";
    return token.length === 1 ? token.toUpperCase() : token;
  });
  const spoken = tokens.map((token) => {
    if (token === "mod") return platform === "apple" ? "Command" : "Control";
    if (token === "enter") return "Enter";
    return token.length === 1 ? token.toUpperCase() : token;
  });
  return { keys, ariaLabel: spoken.join("+") };
}

export function matchesShortcut(event: ShortcutKeyEvent, shortcut: string): boolean {
  const tokens = shortcut.toLowerCase().split("+").filter(Boolean);
  const expectedKey = tokens.find((token) => token !== "mod");
  if (!expectedKey || event.key.toLowerCase() !== expectedKey) return false;
  if (tokens.includes("mod") !== (event.metaKey || event.ctrlKey)) return false;
  return !event.altKey && !event.shiftKey;
}

/** Lets a page-level single-key handler claim an event before shell navigation uses it. */
export function runAfterPageShortcuts(event: Pick<Event, "defaultPrevented">, action: () => void): void {
  queueMicrotask(() => { if (!event.defaultPrevented) action(); });
}
