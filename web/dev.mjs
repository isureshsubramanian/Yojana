// Development server (MOD-02): esbuild watch + serve for the client, and a tiny proxy so the API, /health and
// /notices reach the .NET host on 5080 while the page is served with live reload on 5173. No dependencies.
import { context } from "esbuild";
import { createServer, request as httpRequest } from "node:http";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { buildOptions, writeIndex } from "./build.mjs";

// The development build has its own output folder: web/dist is the production build that the host copies
// and the budget gate measures, and a watcher must never overwrite it with an unminified one.
const devDir = join(dirname(fileURLToPath(import.meta.url)), ".dev");

const apiTarget = new URL(process.env.YOJANA_API ?? "http://localhost:5080");
const port = Number(process.env.PORT ?? 5173);
const proxied = ["/api/", "/health", "/notices"];

const ctx = await context({
  ...buildOptions,
  outdir: devDir,
  minify: false,
  sourcemap: true,
  define: { "process.env.NODE_ENV": '"development"' },
  plugins: [{
    name: "index-html",
    setup(b) { b.onEnd((r) => { if (r.metafile) writeIndex(r.metafile, { dev: true, outdir: devDir }); }); },
  }],
});
await ctx.watch();
const { hosts, port: esbuildPort } = await ctx.serve({ servedir: devDir, fallback: `${devDir}/index.html` });
const esbuildHost = hosts[0] ?? "127.0.0.1";

createServer((req, res) => {
  const toApi = proxied.some((p) => req.url.startsWith(p));
  const options = toApi
    ? { hostname: apiTarget.hostname, port: apiTarget.port, path: req.url, method: req.method, headers: { ...req.headers, host: apiTarget.host } }
    : { hostname: esbuildHost, port: esbuildPort, path: req.url, method: req.method, headers: req.headers };
  const upstream = httpRequest(options, (up) => {
    res.writeHead(up.statusCode ?? 502, up.headers);
    up.pipe(res, { end: true });
  });
  upstream.on("error", (err) => { res.writeHead(502, { "content-type": "text/plain" }); res.end(`upstream unavailable: ${err.message}`); });
  req.pipe(upstream, { end: true });
}).listen(port, () => console.log(`web client on http://localhost:${port} (API proxied to ${apiTarget.origin})`));
