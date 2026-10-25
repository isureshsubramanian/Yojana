import * as z from "zod/mini";

const text = z.string();
const nullableText = z.nullable(text);

export const documentValue = z.object({ type: text, id: text });
export type DocumentValue = z.infer<typeof documentValue>;
const documentValueRequest = z.looseObject({ type: text, id: text });

const itemReferenceLabel = z.object({ code: text, name: nullableText });
const partyReferenceLabel = z.object({ code: text, name: nullableText, partyType: nullableText });
const documentReferenceLabel = z.object({ number: text, status: nullableText, itemId: nullableText });
const locationReferenceLabel = z.object({ code: text, name: nullableText, kind: nullableText });

export const referenceNamesRequest = z.looseObject({
  items: z.array(text),
  parties: z.array(text),
  documents: z.array(documentValueRequest),
  locations: z.array(text),
});
export const referenceNamesResponse = z.object({
  items: z.record(text, itemReferenceLabel),
  parties: z.record(text, partyReferenceLabel),
  documents: z.record(text, documentReferenceLabel),
  locations: z.record(text, locationReferenceLabel),
});
export type ReferenceNamesResponse = z.infer<typeof referenceNamesResponse>;

export const itemFamilyOption = z.object({ code: text, name: text });
export const itemFamilyOptions = z.array(itemFamilyOption);
export type ItemFamilyOption = z.infer<typeof itemFamilyOption>;

export const referenceSearchResult = z.object({
  kind: z.enum(["item", "party", "location", "document", "unit"]),
  id: text,
  type: nullableText,
  code: nullableText,
  name: nullableText,
  partyType: nullableText,
  number: nullableText,
  status: nullableText,
  itemId: nullableText,
});
export const referenceSearchResponse = z.object({ results: z.array(referenceSearchResult) });
export type SearchResult = z.infer<typeof referenceSearchResult>;
