import { test } from "node:test";
import assert from "node:assert/strict";
import { createReferenceStore, documentLabelText, itemLabelText, lotLabelText, MAX_REFERENCE_NAMES, partyLabelText } from "../src/corrections/referenceStore.ts";

const settle = () => new Promise((resolve) => setTimeout(resolve, 0));
const ITEM = "1a2b3c4d-1111-2222-3333-444444444444";
const PARTY = "5e6f7788-1111-2222-3333-444444444444";
const DOCUMENT = { type: "production_order", id: "90abcdef-1111-2222-3333-444444444444" };

test("reference labels use business names and short-id loading fallbacks", () => {
  assert.equal(itemLabelText(ITEM, { code: "FG-100", name: "Field gateway" }), "FG-100 · Field gateway");
  assert.equal(itemLabelText(ITEM, { code: "FG-100", name: null }), "FG-100");
  assert.equal(partyLabelText(PARTY, { code: "C-200", name: "Northwind Plant", partyType: "customer" }), "Northwind Plant (C-200)");
  assert.equal(partyLabelText(PARTY, { code: "C-200", name: null, partyType: null }), "C-200");
  assert.equal(documentLabelText(DOCUMENT, { number: "MO-2026-731", status: "released", itemId: null }), "MO-2026-731");
  assert.equal(itemLabelText(ITEM, undefined), "1A2B3C4D");
  assert.equal(documentLabelText(DOCUMENT, undefined), "90ABCDEF");
});

test("lot labels combine the lot number with the item name without exposing either id", () => {
  const lot = { type: "lot", id: "7061be89-a2b0-5cda-b70a-0b2616e5b9c4" };
  const documentLabel = { number: "KPL-2511", status: null, itemId: ITEM };

  assert.equal(lotLabelText(lot, documentLabel, { code: "PS-1600", name: "1600 W power supply" }), "KPL-2511 · 1600 W power supply");
  assert.equal(lotLabelText(lot, documentLabel, { code: "PS-1600", name: null }), "KPL-2511 · PS-1600");
});

test("references requested in one tick are batched per tenant store and cached", async () => {
  const calls: { items: string[]; parties: string[]; documents: { type: string; id: string }[] }[] = [];
  const store = createReferenceStore(async (request) => {
    calls.push(request);
    return {
      items: Object.fromEntries(request.items.map((id) => [id, { code: `ITEM-${id.slice(0, 8).toUpperCase()}`, name: null }])),
      parties: Object.fromEntries(request.parties.map((id) => [id, { code: "C-200", name: "Northwind Plant", partyType: "customer" }])),
      documents: Object.fromEntries(request.documents.map((document) => [`${document.type}:${document.id}`, { number: "MO-2026-731", status: null, itemId: null }])),
    };
  });
  const itemIds = Array.from({ length: MAX_REFERENCE_NAMES + 1 }, () => crypto.randomUUID());

  store.request({ items: itemIds.slice(0, 100), parties: [PARTY], documents: [DOCUMENT] });
  store.request({ items: itemIds.slice(100), parties: [PARTY], documents: [DOCUMENT] });
  await settle();

  assert.deepEqual(calls.map((call) => [call.items.length, call.parties.length, call.documents.length]), [[200, 1, 1], [1, 0, 0]]);
  assert.equal(store.party(PARTY)?.name, "Northwind Plant");
  assert.equal(store.document(DOCUMENT)?.number, "MO-2026-731");
  store.request({ items: itemIds, parties: [PARTY], documents: [DOCUMENT] });
  await settle();
  assert.equal(calls.length, 2, "known references are not fetched twice");
});
