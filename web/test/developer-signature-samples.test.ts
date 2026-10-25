import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { pathToFileURL } from "node:url";
import test from "node:test";
import { signatureSamples, signatureVector as v } from "../src/settings/developers/signatureSamples.ts";

const header = (signatures: string[], t = v.timestamp) => [`t=${t}`, ...signatures.map((s) => `v1=${s}`)].join(",");
const other = "0".repeat(64);

test("JavaScript sample accepts the reference vector and refuses tampering, staleness and malformed values", async () => {
  const dir = mkdtempSync(join(tmpdir(), "yojana-sig-"));
  try {
    const file = join(dir, "verify.mjs");
    writeFileSync(file, signatureSamples.javascript.code);
    const { isFromYojana } = (await import(pathToFileURL(file).href)) as {
      isFromYojana: (header: string, body: string, secret: string, now?: number) => boolean;
    };
    assert.equal(isFromYojana(header([v.signature]), v.body, v.secret, v.timestamp + 10), true);
    assert.equal(isFromYojana(header([other, v.signature]), v.body, v.secret, v.timestamp), true, "rotation: second v1");
    assert.equal(isFromYojana(header([v.signature]), v.body + " ", v.secret, v.timestamp), false, "body changed");
    assert.equal(isFromYojana(header([v.signature]), v.body, v.secret + "x", v.timestamp), false, "wrong secret");
    assert.equal(isFromYojana(header([v.signature]), v.body, v.secret, v.timestamp + 301), false, "older than 5 min");
    assert.equal(isFromYojana(header(["zz"]), v.body, v.secret, v.timestamp), false, "malformed v1");
    assert.equal(isFromYojana(`v1=${v.signature}`, v.body, v.secret, v.timestamp), false, "no timestamp");
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("Python sample accepts the reference vector and refuses a changed body", (t) => {
  const python = spawnSync("python3", ["--version"]);
  if (python.status !== 0) {
    t.skip("python3 is not installed");
    return;
  }
  const script = `${signatureSamples.python.code}
import sys
ok = is_from_yojana(${JSON.stringify(header([v.signature]))}, ${JSON.stringify(v.body)}.encode(), ${JSON.stringify(v.secret)}, ${v.timestamp + 10})
bad = is_from_yojana(${JSON.stringify(header([v.signature]))}, ${JSON.stringify(v.body + " ")}.encode(), ${JSON.stringify(v.secret)}, ${v.timestamp})
stale = is_from_yojana(${JSON.stringify(header([v.signature]))}, ${JSON.stringify(v.body)}.encode(), ${JSON.stringify(v.secret)}, ${v.timestamp + 301})
sys.exit(0 if ok and not bad and not stale else 1)
`;
  const run = spawnSync("python3", ["-I", "-c", script], { encoding: "utf8" });
  assert.equal(run.status, 0, run.stderr);
});

test("the C# sample keeps the method checked by the .NET test", () => {
  assert.match(signatureSamples.csharp.code, /^static bool IsFromYojana\(string header, string body, string secret, DateTimeOffset now\)/);
});
