// Largest-contentful-paint check for the web shell (MOD-02, MOCKUP-PROCESS §4: LCP ≤ 2.0 s on a mid-range laptop
// over a 10 Mbit link). Drives a Chromium-based browser over the DevTools protocol with Node's built-in WebSocket
// and fetch: no packages, so nothing to license. Also used for the design screenshots (--screenshots <dir>).
//
//   node tools/web-perf/lcp.mjs --base http://localhost:5080 [--budget-ms 2000] [--browser <path>]
//        [--email admin@dev.yojana.local] [--password ...] [--pages /sign-in,/tenants,/] [--screenshots dir] [--no-throttle]
//        [--click "<css selector>"]  clicks the first match on each page after LCP is read (e.g. a row, to open its drawer)
import { spawn } from "node:child_process";
import { existsSync, mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";

const args = Object.fromEntries(process.argv.slice(2).reduce((acc, a, i, all) => {
  if (a.startsWith("--")) acc.push([a.slice(2), all[i + 1] && !all[i + 1].startsWith("--") ? all[i + 1] : "true"]);
  return acc;
}, []));
const base = (args.base ?? "http://localhost:5080").replace(/\/$/, "");
const budget = Number(args["budget-ms"] ?? 2000);
const pages = (args.pages ?? "/sign-in,/tenants,/").split(",").filter(Boolean);
const email = args.email ?? process.env.YOJANA_LCP_EMAIL ?? "admin@dev.yojana.local";
const password = args.password ?? process.env.YOJANA_SEED_PASSWORD ?? "";
const throttle = args["no-throttle"] !== "true";
const shots = args.screenshots;
const click = args.click;

const candidates = [
  args.browser, process.env.YOJANA_BROWSER,
  "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
  "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge",
  "/Applications/Chromium.app/Contents/MacOS/Chromium",
  "/usr/bin/google-chrome", "/usr/bin/google-chrome-stable", "/usr/bin/chromium", "/usr/bin/chromium-browser", "/usr/bin/microsoft-edge",
  "C:/Program Files/Google/Chrome/Application/chrome.exe", "C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe",
].filter(Boolean);
const browser = candidates.find((c) => existsSync(c));
if (!browser) { console.error("No Chromium-based browser found; pass --browser <path> or set YOJANA_BROWSER."); process.exit(2); }

const profile = mkdtempSync(join(tmpdir(), "yojana-lcp-"));
const proc = spawn(browser, ["--headless=new", "--remote-debugging-port=0", `--user-data-dir=${profile}`, "--no-first-run", "--no-default-browser-check", "--disable-extensions", "--hide-scrollbars", "--window-size=1600,1000", "about:blank"], { stdio: ["ignore", "ignore", "pipe"] });
const wsUrl = await new Promise((resolve, reject) => {
  let buf = "";
  proc.stderr.on("data", (d) => { buf += d; const m = buf.match(/DevTools listening on (ws:\/\/\S+)/); if (m) resolve(m[1]); });
  proc.on("exit", (code) => reject(new Error(`browser exited early (${code}): ${buf}`)));
  setTimeout(() => reject(new Error("browser did not expose DevTools in time")), 15000);
});

// Minimal CDP client: one WebSocket, flat sessions, promise per command id.
const ws = new WebSocket(wsUrl);
await new Promise((res, rej) => { ws.onopen = res; ws.onerror = rej; });
let nextId = 1;
const pending = new Map();
const eventWaiters = [];
ws.onmessage = (ev) => {
  const msg = JSON.parse(ev.data);
  if (msg.id && pending.has(msg.id)) { const { resolve, reject } = pending.get(msg.id); pending.delete(msg.id); msg.error ? reject(new Error(msg.error.message)) : resolve(msg.result); return; }
  if (msg.method) for (const w of [...eventWaiters]) if (w.method === msg.method && (!w.sessionId || w.sessionId === msg.sessionId)) { eventWaiters.splice(eventWaiters.indexOf(w), 1); w.resolve(msg.params); }
};
const send = (method, params = {}, sessionId) => new Promise((resolve, reject) => { const id = nextId++; pending.set(id, { resolve, reject }); ws.send(JSON.stringify({ id, method, params, sessionId })); });
const waitFor = (method, sessionId, timeoutMs = 20000) => new Promise((resolve, reject) => { const w = { method, sessionId, resolve }; eventWaiters.push(w); setTimeout(() => { if (eventWaiters.includes(w)) { eventWaiters.splice(eventWaiters.indexOf(w), 1); reject(new Error(`timeout waiting for ${method}`)); } }, timeoutMs); });
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

const { targetId } = await send("Target.createTarget", { url: "about:blank" });
const { sessionId } = await send("Target.attachToTarget", { targetId, flatten: true });
await send("Page.enable", {}, sessionId);
await send("Network.enable", {}, sessionId);
await send("Runtime.enable", {}, sessionId);
await send("Emulation.setDeviceMetricsOverride", { width: 1600, height: 1000, deviceScaleFactor: 1, mobile: false }, sessionId);
await send("Page.addScriptToEvaluateOnNewDocument", { source: `
  window.__lcp = null;
  new PerformanceObserver((list) => { for (const e of list.getEntries()) window.__lcp = { time: e.renderTime || e.loadTime, size: e.size, tag: e.element ? e.element.tagName : null }; })
    .observe({ type: 'largest-contentful-paint', buffered: true });` }, sessionId);

// Sign in once over HTTP and hand the session cookie to the browser, so authenticated pages are measured too.
let signedIn = false;
if (!password) console.warn("no password (--password or YOJANA_SEED_PASSWORD); only anonymous pages are meaningful");
try {
  const r = await fetch(`${base}/api/auth/sign-in`, { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify({ email, password }) });
  const cookies = r.headers.getSetCookie?.() ?? [];
  const session = cookies.map((c) => c.split(";")[0]).find((c) => c.startsWith("yojana.session="));
  if (r.ok && session) {
    const [name, value] = [session.slice(0, session.indexOf("=")), session.slice(session.indexOf("=") + 1)];
    await send("Network.setCookie", { name, value, url: base, httpOnly: true, sameSite: "Strict" }, sessionId);
    signedIn = true;
  } else {
    console.warn(`sign-in as ${email} failed (${r.status}); only anonymous pages are meaningful`);
  }
} catch (e) {
  console.warn(`sign-in request failed: ${e.message}`);
}

if (throttle) {
  // 10 Mbit/s down, 5 Mbit/s up, 40 ms RTT, and a 2x CPU slowdown standing in for a mid-range laptop.
  await send("Network.emulateNetworkConditions", { offline: false, latency: 40, downloadThroughput: 10_000_000 / 8, uploadThroughput: 5_000_000 / 8 }, sessionId);
  await send("Emulation.setCPUThrottlingRate", { rate: 2 }, sessionId);
}

const results = [];
for (const page of pages) {
  await send("Network.clearBrowserCache", {}, sessionId);
  const loaded = waitFor("Page.loadEventFired", sessionId);
  await send("Page.navigate", { url: base + page }, sessionId);
  await loaded;
  // Let the client-side router finish its loaders and paint; LCP candidates keep arriving until input or ~3 s.
  await sleep(2500);
  const { result } = await send("Runtime.evaluate", { expression: "JSON.stringify({ lcp: window.__lcp, url: location.pathname, title: document.title })", returnByValue: true }, sessionId);
  const info = JSON.parse(result.value);
  const lcp = info.lcp ? Math.round(info.lcp.time) : null;
  results.push({ page, landed: info.url, lcp, element: info.lcp?.tag ?? null });
  if (click) {
    await send("Runtime.evaluate", { expression: `document.querySelector(${JSON.stringify(click)})?.click()` }, sessionId);
    await sleep(800);
  }
  if (shots) {
    mkdirSync(shots, { recursive: true });
    const name = page === "/" ? "overview" : page.replace(/^\//, "").replace(/[^a-z0-9-]+/gi, "-");
    for (const mode of ["light", "dark"]) {
      await send("Emulation.setEmulatedMedia", { features: [{ name: "prefers-color-scheme", value: mode }] }, sessionId);
      await sleep(250);
      const shot = await send("Page.captureScreenshot", { format: "png" }, sessionId);
      writeFileSync(join(shots, `${name}-${mode}.png`), Buffer.from(shot.data, "base64"));
    }
    await send("Emulation.setEmulatedMedia", { features: [{ name: "prefers-color-scheme", value: "light" }] }, sessionId);
  }
}

ws.close();
const exited = new Promise((r) => proc.once("exit", r));
proc.kill();
await Promise.race([exited, sleep(5000)]);
try { rmSync(profile, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 }); } catch { /* a leftover temp profile is harmless */ }

console.log(`LCP budget ${budget} ms · ${throttle ? "10 Mbit/s, 40 ms RTT, 2x CPU" : "no throttling"} · ${signedIn ? "signed in as " + email : "anonymous"} · ${browser}`);
let failed = false;
for (const r of results) {
  const status = r.lcp === null ? "NO LCP" : r.lcp <= budget ? "PASS" : "FAIL";
  if (status !== "PASS") failed = true;
  console.log(`[${status}] ${r.page.padEnd(12)} -> ${r.landed.padEnd(12)} LCP ${r.lcp === null ? "—" : r.lcp + " ms"}${r.element ? " (" + r.element.toLowerCase() + ")" : ""}`);
}
process.exit(failed ? 1 : 0);
