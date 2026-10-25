// Webhook signature verification samples shown on Settings › Developer documentation (ADR-0018 D3, D7).
// COUPLING (signature format): these samples, the reference vector below and the Developer module's webhook signer
// describe one format — header "t=<unix seconds>,v1=<hex HMAC-SHA256 of '<t>.<raw body>'>" keyed with the signing
// secret exactly as shown to the administrator, possibly with two v1 values during a rotation. The vector is checked
// against the samples in web/test/developer-signature-samples.test.ts and against the signer in
// tests/Yojana.Developer.DatabaseTests/SignatureSampleTests.cs; changing the signer without these breaks receivers.

export type SampleLanguage = "csharp" | "javascript" | "python";

export const signatureVector = {
  secret: "yjw_7Jq2c9VYbX1mT4rK0pLs8dWfE3nHuQaZgRyCvBoMiNk",
  timestamp: 1794044832,
  body: '{"id":"0193f6a1-7c2e-7b44-9d10-5e2a8c1f0b37","type":"unit.shipped","version":"1"}',
  signature: "d3efb7f84924fffb323a622ec14c7cdf5ef7ccd9a5311bb05caf981f16f60afc",
} as const;

export const signatureSamples: Readonly<Record<SampleLanguage, { label: string; code: string }>> = {
  csharp: {
    label: "C#",
    code: `static bool IsFromYojana(string header, string body, string secret, DateTimeOffset now)
{
    var parts = header.Split(',').Select(p => p.Split('=', 2)).Where(p => p.Length == 2).ToList();
    var t = parts.FirstOrDefault(p => p[0] == "t")?[1];
    if (t is null || !long.TryParse(t, out var seconds)) return false;
    if (Math.Abs(now.ToUnixTimeSeconds() - seconds) > 300) return false;
    var expected = Encoding.ASCII.GetBytes(Convert.ToHexStringLower(
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{t}.{body}"))));
    return parts.Where(p => p[0] == "v1").Any(p =>
        CryptographicOperations.FixedTimeEquals(expected, Encoding.ASCII.GetBytes(p[1])));
}`,
  },
  javascript: {
    label: "JavaScript",
    code: `import { createHmac, timingSafeEqual } from "node:crypto";

export function isFromYojana(header, body, secret, nowSeconds = Math.floor(Date.now() / 1000)) {
  const parts = header.split(",").map((p) => p.split("=", 2)).filter((p) => p.length === 2);
  const t = parts.find((p) => p[0] === "t")?.[1];
  if (!t || !/^\\d+$/.test(t) || Math.abs(nowSeconds - Number(t)) > 300) return false;
  const expected = createHmac("sha256", secret).update(\`\${t}.\${body}\`).digest();
  return parts.some(([k, v]) => k === "v1" && /^[0-9a-f]{64}$/.test(v)
    && timingSafeEqual(expected, Buffer.from(v, "hex")));
}`,
  },
  python: {
    label: "Python",
    code: `import hashlib, hmac, time

def is_from_yojana(header: str, body: bytes, secret: str, now: float | None = None) -> bool:
    parts = [p.split("=", 1) for p in header.split(",") if "=" in p]
    t = next((v for k, v in parts if k == "t"), None)
    if t is None or not t.isdigit() or abs((now or time.time()) - int(t)) > 300:
        return False
    expected = hmac.new(secret.encode(), t.encode() + b"." + body, hashlib.sha256).hexdigest()
    return any(k == "v1" and hmac.compare_digest(expected, v) for k, v in parts)`,
  },
};
