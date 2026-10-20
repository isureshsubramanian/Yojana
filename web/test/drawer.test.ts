import { test } from "node:test";
import assert from "node:assert/strict";
import { closeDrawer, drawerState, openDrawer, setDrawerContext, showDrawerContext, toggleDrawer, updateDrawer } from "../src/shell/drawerStore.ts";

test("the drawer opens for a row, keeps the screen context, and closes clean", () => {
  setDrawerContext("context", "This lookup");
  assert.equal(drawerState().open, false);
  openDrawer({ eyebrow: "Selected unit", title: "2506-493445", mono: true }, "row body");
  assert.equal(drawerState().open, true);
  assert.equal(drawerState().head?.title, "2506-493445");
  assert.equal(drawerState().context, "context");
  updateDrawer({ eyebrow: "Recorded", title: "done" }, "after");
  assert.equal(drawerState().selected, "after");
  closeDrawer();
  assert.equal(drawerState().open, false);
  assert.equal(drawerState().head, null);
  assert.equal(drawerState().selected, null);
  assert.equal(drawerState().context, "context");
});

test("the header button shows the screen context alone and toggles", () => {
  closeDrawer();
  showDrawerContext();
  assert.equal(drawerState().open, true);
  assert.equal(drawerState().head, null);
  toggleDrawer();
  assert.equal(drawerState().open, false);
  updateDrawer({ eyebrow: "x", title: "y" }, "ignored while closed");
  assert.equal(drawerState().selected, null);
});

test("re-registering the screen context leaves the open snapshot unchanged, so a page reading it does not re-render", () => {
  closeDrawer();
  const before = drawerState();
  setDrawerContext("context again", "This lookup");
  assert.notEqual(drawerState(), before);
  assert.equal(drawerState().open, before.open);
});
