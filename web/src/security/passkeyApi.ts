import { api } from "../api/client";
import type { Session } from "../api/types";
import { assertionJson, attestationJson, creationOptions, requestOptions, type CreationOptionsJson, type RequestOptionsJson } from "./webauthn";

export type PasskeyMode = "passwordless" | "second_step";
export interface Passkey { id: string; name: string; createdAt: string; lastUsedAt: string | null; synced: boolean }
export interface PasskeyAccount { allowed: boolean; passwordless: boolean; passkeys: Passkey[] }
export interface PasskeySettings { allowed: boolean; passwordless: boolean; version: string }

export const passkeyApi = {
  account: (signal?: AbortSignal) => api<PasskeyAccount>("/api/auth/passkeys", signal ? { signal } : undefined),
  /** Asks for the current password, then for the device's fingerprint, face or PIN. */
  add: async (name: string, currentPassword: string) => {
    const start = await api<{ ceremonyId: string; options: CreationOptionsJson }>("/api/auth/passkeys/registration/start", { method: "POST", body: { name, currentPassword } });
    const credential = await navigator.credentials.create({ publicKey: creationOptions(start.options) }) as PublicKeyCredential | null;
    if (!credential) throw new DOMException("No passkey was created.", "NotAllowedError");
    return api<Passkey>("/api/auth/passkeys/registration/finish", { method: "POST", body: { ceremonyId: start.ceremonyId, credential: attestationJson(credential) } });
  },
  remove: (id: string, currentPassword: string) => api<void>(`/api/auth/passkeys/${id}/remove`, { method: "POST", body: { currentPassword } }),
  signIn: async (mode: PasskeyMode) => {
    const start = await api<{ ceremonyId: string; options: RequestOptionsJson }>("/api/auth/passkey/sign-in/start", { method: "POST", body: { mode } });
    const credential = await navigator.credentials.get({ publicKey: requestOptions(start.options) }) as PublicKeyCredential | null;
    if (!credential) throw new DOMException("No passkey was chosen.", "NotAllowedError");
    return api<Session>("/api/auth/passkey/sign-in/finish", { method: "POST", body: { ceremonyId: start.ceremonyId, mode, credential: assertionJson(credential) } });
  },
  settings: (signal?: AbortSignal) => api<PasskeySettings>("/api/security/settings/passkeys", signal ? { signal } : undefined),
  saveSettings: (settings: PasskeySettings) => api<PasskeySettings>("/api/security/settings/passkeys", { method: "PUT", body: settings }),
};
