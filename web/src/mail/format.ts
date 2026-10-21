import type { Tone } from "../components/types.ts";
import type { MailBlock, MailField, MailLanguageContent, MailRecipientInput, MailTemplateRow, SentMailRow } from "./api.ts";

// Who sees what. The server's policies decide (YojanaPolicy.CustomerMailDesigner, CustomerMailSender and TenantAdministrator
// in PlatformSetup); these lists only hide screens a person could not use. Change both together.
export const DESIGNER_ROLES = ["tenant_administrator", "certificate_administrator", "service_manager", "customer_service"] as const;
export const SENDER_ROLES = ["tenant_administrator", "service_desk", "service_supervisor", "service_manager", "customer_service"] as const;
export const holds = (roles: readonly string[], allowed: readonly string[]) => roles.some((role) => allowed.includes(role));

/** Same names as MailTemplateComposer.LanguageName, so a check message and a tab say the same thing. */
export function languageName(code: string): string {
  switch (code.toLowerCase()) {
    case "en": return "English";
    case "de": return "Deutsch";
    case "fr": return "Français";
    case "es": return "Español";
    case "it": return "Italiano";
    case "nl": return "Nederlands";
    case "hi": return "हिन्दी";
    default: return code;
  }
}

export const LANGUAGE_CHOICES = ["en", "de", "fr", "es", "it", "nl", "hi"] as const;

export function templateState(row: Pick<MailTemplateRow, "retired" | "publishedVersion" | "draftVersion" | "waitingForReview">): { tone: Tone; text: string } {
  if (row.retired) return { tone: "neutral", text: "Retired" };
  if (row.publishedVersion !== null) return { tone: "ok", text: `v${row.publishedVersion} published` };
  if (row.waitingForReview) return { tone: "info", text: `v${row.draftVersion ?? 1} waiting for review` };
  return { tone: "warn", text: `v${row.draftVersion ?? 1} draft` };
}

export function versionTone(status: string): Tone {
  return status === "published" ? "ok" : status === "draft" ? "warn" : "neutral";
}

/** Delivery as the person who sent it thinks of it: delivered, still trying, or not delivered and why. */
export function deliveryState(row: Pick<SentMailRow, "status" | "attempts" | "lastError">): { tone: Tone; text: string } {
  switch (row.status) {
    case "sent": return { tone: "ok", text: "Delivered" };
    case "queued": return { tone: "info", text: "Queued" };
    case "retrying": return { tone: "warn", text: `Retrying · attempt ${row.attempts}` };
    case "failed": return { tone: "bad", text: refusedAddress(row.lastError) ? "Refused address" : "Not delivered" };
    default: return { tone: "neutral", text: row.status };
  }
}

/** A permanent refusal naming the recipient (SMTP 550/551/553, or the provider's own word for it). */
export function refusedAddress(error: string | null): boolean {
  return !!error && /\b55[013]\b|recipient|mailbox unavailable|no such user/i.test(error);
}

export const BLOCK_TYPES: readonly { type: string; label: string; hint: string }[] = [
  { type: "heading", label: "Heading", hint: "A sub-heading inside the message" },
  { type: "paragraph", label: "Paragraph", hint: "Text with fields; **bold**, *italic* and [links]({{links.…}})" },
  { type: "table", label: "Table", hint: "One row per item of a list field, with the sentence shown when it is empty" },
  { type: "panel", label: "Details panel", hint: "Up to eight label and value rows" },
  { type: "button", label: "Button", hint: "A link into the product, never elsewhere" },
  { type: "sign_off", label: "Sign-off", hint: "The closing lines" },
];

export function blockLabel(block: Pick<MailBlock, "type" | "when">): string {
  if (block.type === "paragraph" && block.when === "message.note") return "Personal note";
  return BLOCK_TYPES.find((entry) => entry.type === block.type)?.label ?? block.type;
}

export function newBlock(type: string, fields: readonly MailField[]): MailBlock {
  switch (type) {
    case "table": {
      const list = fields.find((field) => field.repeats);
      const items = list ? fields.filter((field) => field.itemOf === list.path).slice(0, 2) : [];
      return { type, each: list?.path ?? "", emptyText: "Nothing to show.", columns: items.length
        ? items.map((field) => ({ header: field.label, text: `{{${itemToken(field)}}}` })) : [{ header: "Column", text: "" }] };
    }
    case "panel": return { type, facts: [{ label: "Label", text: "" }] };
    case "button": return { type, label: "Open", link: fields.find((field) => field.path.startsWith("links."))?.path ?? "" };
    case "sign_off": return { type, text: "Kind regards,\n{{sender.name}}" };
    default: return { type, text: "" };
  }
}

export function moveBlock<T>(items: readonly T[], index: number, delta: -1 | 1): T[] {
  const target = index + delta;
  if (target < 0 || target >= items.length) return [...items];
  const next = [...items];
  [next[index], next[target]] = [next[target]!, next[index]!];
  return next;
}

/** Inside a table the item fields are written without their list's name: covers.family_name → family_name. */
export function itemToken(field: Pick<MailField, "path" | "itemOf">): string {
  return field.itemOf ? field.path.slice(field.itemOf.length + 1) : field.path;
}

/** Puts {{field}} at the caret (or over the selection) and says where the caret goes next. */
export function insertField(text: string, token: string, start: number, end: number): { text: string; caret: number } {
  const placed = `{{${token}}}`;
  const from = Math.max(0, Math.min(start, text.length));
  const to = Math.max(from, Math.min(end, text.length));
  return { text: text.slice(0, from) + placed + text.slice(to), caret: from + placed.length };
}

export type TextPiece = { kind: "text"; value: string } | { kind: "field"; value: string } | { kind: "logic"; value: string };

/** Splits author text into plain text, field chips and #if/#each/else logic, for the block list. */
export function pieces(text: string | null | undefined): TextPiece[] {
  const result: TextPiece[] = [];
  const pattern = /\{\{\s*([^}]+?)\s*\}\}/g;
  let last = 0;
  for (let match = pattern.exec(text ?? ""); match; match = pattern.exec(text ?? "")) {
    if (match.index > last) result.push({ kind: "text", value: text!.slice(last, match.index) });
    const inner = match[1]!;
    result.push(/^[#/]|^else$/.test(inner) ? { kind: "logic", value: inner } : { kind: "field", value: inner });
    last = match.index + match[0].length;
  }
  if (last < (text ?? "").length) result.push({ kind: "text", value: text!.slice(last) });
  return result;
}

/** A new language starts as a copy of the one on screen, so the translator replaces words rather than rebuilding blocks. */
export function addLanguage(languages: readonly MailLanguageContent[], code: string, from: MailLanguageContent): MailLanguageContent[] {
  if (languages.some((language) => language.language === code)) return [...languages];
  return [...languages, { ...structuredClone(from), language: code }];
}

const ADDRESS = /^[^\s@<>()",;:]+@[^\s@<>()",;:]+\.[^\s@<>()",;:]+$/;
export const looksLikeAddress = (value: string) => ADDRESS.test(value.trim());

/** A typed address is offered for confirmation unless it is already one of the customer's contacts. */
export function recipientFor(address: string, contacts: readonly { address: string; name: string }[], confirmed: boolean): MailRecipientInput {
  const known = contacts.find((contact) => contact.address.toLowerCase() === address.trim().toLowerCase());
  return known ? { address: known.address, name: known.name } : { address: address.trim(), confirmed };
}

export function fieldGroups(fields: readonly MailField[]): [string, MailField[]][] {
  const groups = new Map<string, MailField[]>();
  for (const field of fields) groups.set(field.group, [...(groups.get(field.group) ?? []), field]);
  return [...groups.entries()];
}

/** Every field path a language's blocks mention, for the "Used" marks in the field list. */
export function fieldsMentioned(language: MailLanguageContent, fields: readonly MailField[]): Set<string> {
  const used = new Set<string>();
  const add = (text: string | null | undefined, list: string | null) => {
    for (const piece of pieces(text)) {
      if (piece.kind === "field") used.add(list && fields.some((field) => field.path === `${list}.${piece.value}`) ? `${list}.${piece.value}` : piece.value);
      else if (piece.kind === "logic") { const path = piece.value.split(/\s+/)[1]; if (path) used.add(path); }
    }
  };
  for (const text of [language.subject, language.preheader, language.bandLabel, language.heading]) add(text, null);
  for (const block of language.blocks) {
    const list = block.each?.trim() || null;
    if (list) used.add(list);
    if (block.when) used.add(block.when.trim());
    if (block.link) used.add(block.link.trim());
    for (const text of [block.text, block.emptyText, block.label]) add(text, null);
    for (const column of block.columns ?? []) add(column.text, list);
    for (const fact of block.facts ?? []) add(fact.text, null);
  }
  return used;
}

/**
 * Names one editable text of a language so the field list can insert at the caret of whichever box was last used:
 * subject, preheader, bandLabel, heading, or b{block}.{text|emptyText|label}, b{block}.c{column}.{header|text},
 * b{block}.f{fact}.{label|text}.
 */
export type TextKey = string;

const BLOCK_KEY = /^b(\d+)\.(?:(c|f)(\d+)\.)?(\w+)$/;

export function readText(language: MailLanguageContent, key: TextKey): string {
  if (key === "subject" || key === "bandLabel" || key === "heading") return language[key];
  if (key === "preheader") return language.preheader ?? "";
  const match = BLOCK_KEY.exec(key);
  const block = match ? language.blocks[Number(match[1])] : undefined;
  if (!match || !block) return "";
  const [, , part, index, name] = match;
  const record = (part === "c" ? block.columns?.[Number(index)] : part === "f" ? block.facts?.[Number(index)] : block) as Record<string, unknown> | undefined;
  const value = record?.[name!];
  return typeof value === "string" ? value : "";
}

export function writeText(language: MailLanguageContent, key: TextKey, value: string): MailLanguageContent {
  if (key === "subject" || key === "bandLabel" || key === "heading") return { ...language, [key]: value };
  if (key === "preheader") return { ...language, preheader: value || null };
  const match = BLOCK_KEY.exec(key);
  if (!match || !language.blocks[Number(match[1])]) return language;
  const [, blockIndex, part, index, name] = match;
  const blocks = language.blocks.map((block, at) => {
    if (at !== Number(blockIndex)) return block;
    if (part === "c") return { ...block, columns: (block.columns ?? []).map((column, j) => j === Number(index) ? { ...column, [name!]: value } : column) };
    if (part === "f") return { ...block, facts: (block.facts ?? []).map((fact, j) => j === Number(index) ? { ...fact, [name!]: value } : fact) };
    return { ...block, [name!]: value };
  });
  return { ...language, blocks };
}

/** The list a text key repeats over: the table's own list for its column values, otherwise none. */
export function listOf(language: MailLanguageContent, key: TextKey): string | null {
  const match = BLOCK_KEY.exec(key);
  if (!match || match[2] !== "c" || match[4] !== "text") return null;
  return language.blocks[Number(match[1])]?.each?.trim() || null;
}
