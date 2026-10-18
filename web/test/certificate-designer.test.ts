import assert from "node:assert/strict";
import test from "node:test";
import { assetPreviewSource, designPreviewHtml, insertPlaceholder } from "../src/certificates/designerCore.ts";

test("field insertion replaces the selected HTML range and returns the next caret", () => {
  const result = insertPlaceholder("<p>old value</p>", "model.code", 3, 12);

  assert.equal(result.text, "<p>{{model.code}}</p>");
  assert.equal(result.caret, 17);
});

test("design preview highlights placeholders without granting script or network access", () => {
  const html = designPreviewHtml("<p>{{model.code}}</p><script>unsafe()</script>");

  assert.match(html, /<mark class="placeholder">model\.code<\/mark>/);
  assert.match(html, /default-src 'none'/);
  assert.ok(!html.includes("https:"));
});

test("SVG logo preview uses the authenticated tenant asset endpoint", () => {
  const source = assetPreviewSource({ assetId: "0199a4be-7532-7000-8000-000000000001", mediaType: "image/svg+xml" });

  assert.equal(source, "/api/tenant-assets/0199a4be-7532-7000-8000-000000000001/content");
  assert.equal(assetPreviewSource({ assetId: "bad", mediaType: "text/html" }), null);
});
