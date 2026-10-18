import assert from "node:assert/strict";
import test from "node:test";
import { assetPreviewSource, designPreviewHtml, insertPlaceholder } from "../src/certificates/designerCore.ts";
import { formLabel } from "../src/certificates/format.ts";

test("field insertion replaces the selected HTML range and returns the next caret", () => {
  const result = insertPlaceholder("<p>old value</p>", "model.code", 3, 12);

  assert.equal(result.text, "<p>{{model.code}}</p>");
  assert.equal(result.caret, 17);
});

test("certificate form names are readable and warranty never falls back to custom", () => {
  assert.equal(formLabel("coc", "release_record"), "Certificate of conformity");
  assert.equal(formLabel("custom", "warranty_certificate"), "Warranty certificate");
  assert.equal(formLabel("release_8130_3_style", "release_record"), "8130-3-style release block");
});

test("design preview highlights placeholders without granting script or network access", () => {
  const html = designPreviewHtml("<p>{{model.code}}</p><script>unsafe()</script>");

  assert.match(html, /<mark class="placeholder">model\.code<\/mark>/);
  assert.match(html, /default-src 'none'/);
  assert.ok(!html.includes("https:"));
});

test("design preview labels control blocks and keeps template markers out of attributes", () => {
  const html = designPreviewHtml(`<table><tbody>{{#each identifiers}}<tr class="status-{{type}}"><td>{{label type}}</td>{{#if issuer}}<td>{{issuer}}</td>{{/if}}</tr>{{else}}<tr><td>None</td></tr>{{/each}}</tbody></table>`);

  assert.match(html, /Repeats for each identifier/);
  assert.match(html, /Shown only when issuer has a value/);
  assert.match(html, /<mark class="placeholder">label · type<\/mark>/);
  assert.ok(!html.includes("#each"));
  assert.ok(!html.includes("/each"));
  assert.ok(!html.includes('status">'));
});

test("SVG logo preview uses the authenticated tenant asset endpoint", () => {
  const source = assetPreviewSource({ assetId: "0199a4be-7532-7000-8000-000000000001", mediaType: "image/svg+xml" });

  assert.equal(source, "/api/tenant-assets/0199a4be-7532-7000-8000-000000000001/content");
  assert.equal(assetPreviewSource({ assetId: "bad", mediaType: "text/html" }), null);
});
