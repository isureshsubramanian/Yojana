import * as z from "zod/mini";
import { apiParsed } from "../api/client.ts";
import { session } from "../api/types.ts";
import { assertionJson, attestationJson, creationOptions, requestOptions, type CreationOptionsJson, type RequestOptionsJson } from "./webauthn.ts";

export type PasskeyMode = "passwordless" | "second_step";
export const passkey = z.object({ id: z.string(), name: z.string(), createdAt: z.string(), lastUsedAt: z.nullable(z.string()), synced: z.boolean() });
export type Passkey = z.infer<typeof passkey>;
export const passkeyAccount = z.object({ allowed: z.boolean(), passwordless: z.boolean(), passkeys: z.array(passkey) });
export type PasskeyAccount = z.infer<typeof passkeyAccount>;
export const passkeySettings = z.object({ allowed: z.boolean(), passwordless: z.boolean(), version: z.string() });
export type PasskeySettings = z.infer<typeof passkeySettings>;

// The ceremony options go to navigator.credentials as the server sent them (rp, pubKeyCredParams, timeout and the rest), so
// these schemas check the fields the browser half converts and keep every other field instead of dropping it.
const descriptor = z.looseObject({ type: z.literal("public-key"), id: z.string(), transports: z.optional(z.array(z.string())) });
export const creationOptionsJson: z.ZodMiniType<CreationOptionsJson> = z.looseObject({ challenge: z.string(),
  user: z.looseObject({ id: z.string(), name: z.string(), displayName: z.string() }), excludeCredentials: z.optional(z.nullable(z.array(descriptor))) });
export const requestOptionsJson: z.ZodMiniType<RequestOptionsJson> = z.looseObject({ challenge: z.string(), allowCredentials: z.optional(z.nullable(z.array(descriptor))) });
const ceremony = <T extends z.ZodMiniType>(options: T) => z.object({ ceremonyId: z.string(), options });

export const passkeyApi = {
  account: (signal?: AbortSignal) => apiParsed("/api/auth/passkeys", passkeyAccount, signal ? { signal } : undefined),
  /** Asks for the current password, then for the device's fingerprint, face or PIN. */
  add: async (name: string, currentPassword: string) => {
    const start = await apiParsed("/api/auth/passkeys/registration/start", ceremony(creationOptionsJson), { method: "POST", body: { name, currentPassword } });
    const credential = await navigator.credentials.create({ publicKey: creationOptions(start.options) }) as PublicKeyCredential | null;
    if (!credential) throw new DOMException("No passkey was created.", "NotAllowedError");
    return apiParsed("/api/auth/passkeys/registration/finish", passkey, { method: "POST", body: { ceremonyId: start.ceremonyId, credential: attestationJson(credential) } });
  },
  remove: (id: string, currentPassword: string) => apiParsed(`/api/auth/passkeys/${id}/remove`, z.unknown(), { method: "POST", body: { currentPassword } }),
  signIn: async (mode: PasskeyMode) => {
    const start = await apiParsed("/api/auth/passkey/sign-in/start", ceremony(requestOptionsJson), { method: "POST", body: { mode } });
    const credential = await navigator.credentials.get({ publicKey: requestOptions(start.options) }) as PublicKeyCredential | null;
    if (!credential) throw new DOMException("No passkey was chosen.", "NotAllowedError");
    return apiParsed("/api/auth/passkey/sign-in/finish", session, { method: "POST", body: { ceremonyId: start.ceremonyId, mode, credential: assertionJson(credential) } });
  },
  settings: (signal?: AbortSignal) => apiParsed("/api/security/settings/passkeys", passkeySettings, signal ? { signal } : undefined),
  saveSettings: (settings: PasskeySettings) => apiParsed("/api/security/settings/passkeys", passkeySettings, { method: "PUT", body: settings }),
};
