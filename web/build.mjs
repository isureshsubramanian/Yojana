// Production build of the web client (MOD-02). esbuild only: bundles, splits per lazy route, hashes file names,
// then writes dist/index.html from index.html with the eager assets injected. The output folder is read by
// two other places, which is a declared coupling: tools/gates/budgets.json (bundle budget) and
// src/Web/Yojana.Web/Yojana.Web.csproj (copies dist into the host's wwwroot). Change all three together.
import { build } from "esbuild";
import { readFileSync, writeFileSync, rmSync, mkdirSync, statSync } from "node:fs";
import { gzipSync } from "node:zlib";
import { join, dirname, relative } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const dist = join(here, "dist");

export const buildOptions = {
  entryPoints: [join(here, "src/main.tsx")],
  bundle: true,
  splitting: true,
  format: "esm",
  platform: "browser",
  target: ["es2022", "chrome110", "edge110", "firefox115", "safari16"],
  outdir: dist,
  entryNames: "assets/[name]-[hash]",
  chunkNames: "assets/[name]-[hash]",
  assetNames: "assets/[name]-[hash]",
  jsx: "automatic",
  legalComments: "none",
  metafile: true,
  logLevel: "warning",
};

export function writeIndex(metafile, { dev = false } = {}) {
  const outputs = metafile.outputs;
  const entryPath = Object.keys(outputs).find((f) => outputs[f].entryPoint && outputs[f].entryPoint.endsWith("src/main.tsx"));
  if (!entryPath) throw new Error("entry output not found in metafile");
  const url = (f) => "/" + relative(dist, join(here, f));
  // Chunks the entry imports statically load before first paint, so they are preloaded here and counted by the
  // budget gate; dynamic imports (lazy routes) are neither.
  const eager = [];
  const seen = new Set([entryPath]);
  const queue = [entryPath];
  while (queue.length) {
    const f = queue.shift();
    for (const imp of outputs[f].imports ?? []) {
      if (imp.kind === "import-statement" && !imp.external && !seen.has(imp.path)) { seen.add(imp.path); eager.push(imp.path); queue.push(imp.path); }
    }
  }
  const cssFile = outputs[entryPath].cssBundle ? url(outputs[entryPath].cssBundle) : null;
  const tags = [
    cssFile ? `<link rel="stylesheet" href="${cssFile}">` : "",
    ...eager.map((f) => `<link rel="modulepreload" href="${url(f)}">`),
    `<script type="module" src="${url(entryPath)}"></script>`,
    dev ? `<script>new EventSource('/esbuild').addEventListener('change', () => location.reload());</script>` : "",
  ].filter(Boolean).join("\n");
  const html = readFileSync(join(here, "index.html"), "utf8").replace("<!--@assets-->", tags);
  writeFileSync(join(dist, "index.html"), html);
  return { js: url(entryPath), css: cssFile, eager: eager.map(url) };
}

function gz(path) { return gzipSync(readFileSync(path), { level: 9 }).length; }

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  rmSync(dist, { recursive: true, force: true });
  mkdirSync(dist, { recursive: true });
  const result = await build({ ...buildOptions, minify: true, sourcemap: false, define: { "process.env.NODE_ENV": '"production"' } });
  const { js, css, eager } = writeIndex(result.metafile);
  const rows = Object.keys(result.metafile.outputs).map((f) => {
    const abs = join(here, f);
    return { file: relative(dist, abs), bytes: statSync(abs).size, gzip: gz(abs) };
  });
  for (const r of rows.sort((a, b) => b.gzip - a.gzip)) {
    console.log(`${String(r.gzip).padStart(8)} B gz  ${String(r.bytes).padStart(8)} B  ${r.file}`);
  }
  console.log(`eager: ${[js, ...eager, css].filter(Boolean).join(" + ")}`);
}
