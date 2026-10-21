import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import { assertionJson, creationOptions, fromBase64Url, requestOptions, toBase64Url } from "../src/security/webauthn.ts";

const source = (path: string) => readFileSync(new URL(`../src/${path}`, import.meta.url), "utf8");

test("base64url round-trips the bytes WebAuthn exchanges, without padding", () => {
  const bytes = new Uint8Array([0, 1, 2, 250, 251, 252, 253, 254, 255]);
  const encoded = toBase64Url(bytes.buffer);
  assert.equal(encoded, "AAEC-vv8_f7_");
  assert.deepEqual(new Uint8Array(fromBase64Url(encoded)), bytes);
  assert.deepEqual(new Uint8Array(fromBase64Url("AQ")), new Uint8Array([1]));
});

test("server options become the buffers navigator.credentials expects", () => {
  const created = creationOptions({ challenge: "AQID", user: { id: "BAU", name: "a@example.invalid", displayName: "A" }, excludeCredentials: [{ type: "public-key", id: "CQk" }], rp: { id: "localhost", name: "Yojana" } });
  assert.deepEqual(new Uint8Array(created.challenge as ArrayBuffer), new Uint8Array([1, 2, 3]));
  assert.deepEqual(new Uint8Array(created.user.id as ArrayBuffer), new Uint8Array([4, 5]));
  assert.deepEqual(new Uint8Array(created.excludeCredentials![0]!.id as ArrayBuffer), new Uint8Array([9, 9]));
  assert.equal(created.rp.id, "localhost");
  const requested = requestOptions({ challenge: "AQID", userVerification: "required" });
  assert.deepEqual(requested.allowCredentials, []);
  assert.equal(requested.userVerification, "required");
});

test("an answer is sent back as base64url JSON with the user handle", () => {
  const buffer = (...values: number[]) => new Uint8Array(values).buffer;
  const answer = assertionJson({ id: "AQ", rawId: buffer(1), type: "public-key", getClientExtensionResults: () => ({}),
    response: { authenticatorData: buffer(2), clientDataJSON: buffer(3), signature: buffer(4), userHandle: buffer(5) } } as unknown as PublicKeyCredential);
  assert.deepEqual(answer.response, { authenticatorData: "Ag", clientDataJSON: "Aw", signature: "BA", userHandle: "BQ" });
  assert.equal(answer.rawId, "AQ");
});

test("sign-in offers a passkey alone and as the second step, as in the approved mockup", () => {
  const signIn = source("pages/SignIn.tsx");
  for (const phrase of ["Sign in with a passkey", "Confirm on your device", "Nothing is sent to us but a signed answer", "A passkey counts as both steps", "Waiting for your device…", "Use my password instead", "Use a passkey instead", "other ways"]) assert.match(signIn, new RegExp(phrase));
  assert.match(signIn, /passkeyApi\.signIn\("passwordless"\)/); assert.match(signIn, /passkeyApi\.signIn\("second_step"\)/);
  assert.match(signIn, /autoComplete="username webauthn"/);
});

test("account security lists passkeys and asks for the current password to add or remove one", () => {
  const account = source("pages/AccountSecurity.tsx");
  for (const phrase of ["Passkeys", "Add a passkey", "Removing a passkey e-mails you", "Current password", "count as the second step", "Last used"]) assert.match(account, new RegExp(phrase));
  assert.match(account, /passkeyApi\.add\(name, password\)/); assert.match(account, /passkeyApi\.remove\(dialog\.remove, value\)/);
});

test("the sign-in policy has the two passkey settings and Users shows passkeys", () => {
  const policy = source("settings/SignInPolicy.tsx"); const users = source("settings/Users.tsx");
  for (const phrase of ["Allow passkeys", "Allow signing in with a passkey alone", "A passkey always counts as the second step"]) assert.match(policy, new RegExp(phrase));
  assert.match(policy, /passkeyApi\.saveSettings/);
  assert.match(users, /passkeyCount/); assert.match(users, /method === "passkey"/);
});

test("once a passkey alone may sign someone in, this browser's sign-in page leads with it", () => {
  const signIn = source("pages/SignIn.tsx"); const account = source("pages/AccountSecurity.tsx"); const hint = source("security/passkeyHint.ts");
  assert.match(signIn, /useState\(\(\) => !\(passkeyFirst\(\) && passkeysSupported\(\)\)\)/);
  assert.match(signIn, /This browser remembers you last signed in with a passkey/);
  assert.match(signIn, /rememberPasskeyFirst\(account\.allowed && account\.passwordless && account\.passkeys\.length > 0\)/);
  assert.match(signIn, /passkey_not_allowed"\) \{ rememberPasskeyFirst\(false\)/);
  assert.match(account, /rememberPasskeyFirst\(value\.allowed && value\.passwordless && value\.passkeys\.length > 0\)/);
  assert.match(hint, /try \{/); assert.doesNotMatch(hint, /email\b.*setItem/);
});
