import { test } from "node:test";
import assert from "node:assert/strict";
import { createNameStore, MAX_REFERENCES, personLabel } from "../src/people/peopleStore.ts";

const settle = () => new Promise((resolve) => setTimeout(resolve, 0));
const ID = "01a13fdc-6b2e-7c11-9a3f-0123456789ab";

test("a reference without a name shows as given, an id cut short", () => {
  assert.equal(personLabel(ID, "Asha Rao"), "Asha Rao");
  assert.equal(personLabel(ID, undefined), "01a13fdc…");
  assert.equal(personLabel("desk@example.test", null), "desk@example.test");
  assert.equal(personLabel(null, null), "—");
});

test("references asked for in one tick go to the server once and are cached", async () => {
  const calls: string[][] = [];
  const store = createNameStore(async (references) => { calls.push(references); return { [ID]: "Asha Rao" }; });
  let notified = 0;
  store.subscribe(() => { notified += 1; });

  store.request([ID, "desk@example.test", null, ID]);
  store.request(["desk@example.test", undefined]);
  await settle();

  assert.deepEqual(calls, [[ID, "desk@example.test"]]);
  assert.equal(store.name(ID), "Asha Rao");
  assert.equal(store.name("desk@example.test"), undefined);
  assert.equal(notified, 1);

  store.request([ID, "desk@example.test"]);
  await settle();
  assert.equal(calls.length, 1, "known and unresolvable references are not asked for again");
});

test("large batches are split at the server's limit and a failed request leaves references as given", async () => {
  const sizes: number[] = [];
  const store = createNameStore(async (references) => { sizes.push(references.length); throw new Error("offline"); });
  store.request(Array.from({ length: MAX_REFERENCES + 3 }, (_, i) => `user${i}@example.test`));
  await settle();
  assert.deepEqual(sizes, [MAX_REFERENCES, 3]);
  assert.equal(store.name("user0@example.test"), undefined);
});
