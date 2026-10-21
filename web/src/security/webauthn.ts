// The browser half of a passkey ceremony, from the owner's TapInAuth passkey script: the server's options arrive as
// JSON with base64url strings, navigator.credentials wants ArrayBuffers, and its answer goes back as base64url JSON.

export function fromBase64Url(value: string): ArrayBuffer {
  const base64 = value.replace(/-/g, "+").replace(/_/g, "/");
  const binary = atob(base64 + "=".repeat((4 - (base64.length % 4)) % 4));
  const bytes = new Uint8Array(binary.length);
  for (let index = 0; index < binary.length; index++) bytes[index] = binary.charCodeAt(index);
  return bytes.buffer;
}

export function toBase64Url(buffer: ArrayBuffer): string {
  const bytes = new Uint8Array(buffer);
  let binary = "";
  for (const byte of bytes) binary += String.fromCharCode(byte);
  return btoa(binary).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
}

type Descriptor = { type: "public-key"; id: string; transports?: string[] };
export interface CreationOptionsJson { challenge: string; user: { id: string; name: string; displayName: string }; excludeCredentials?: Descriptor[]; [key: string]: unknown }
export interface RequestOptionsJson { challenge: string; allowCredentials?: Descriptor[]; [key: string]: unknown }

const descriptors = (list: Descriptor[] | undefined) => (list ?? []).map((item) => ({ ...item, id: fromBase64Url(item.id) })) as PublicKeyCredentialDescriptor[];

export function creationOptions(json: CreationOptionsJson): PublicKeyCredentialCreationOptions {
  return { ...json, challenge: fromBase64Url(json.challenge), user: { ...json.user, id: fromBase64Url(json.user.id) }, excludeCredentials: descriptors(json.excludeCredentials) } as unknown as PublicKeyCredentialCreationOptions;
}

export function requestOptions(json: RequestOptionsJson): PublicKeyCredentialRequestOptions {
  return { ...json, challenge: fromBase64Url(json.challenge), allowCredentials: descriptors(json.allowCredentials) } as unknown as PublicKeyCredentialRequestOptions;
}

export function attestationJson(credential: PublicKeyCredential) {
  const response = credential.response as AuthenticatorAttestationResponse;
  return { id: credential.id, rawId: toBase64Url(credential.rawId), type: credential.type, clientExtensionResults: credential.getClientExtensionResults(), response: { attestationObject: toBase64Url(response.attestationObject), clientDataJSON: toBase64Url(response.clientDataJSON), transports: typeof response.getTransports === "function" ? response.getTransports() : [] } };
}

export function assertionJson(credential: PublicKeyCredential) {
  const response = credential.response as AuthenticatorAssertionResponse;
  return { id: credential.id, rawId: toBase64Url(credential.rawId), type: credential.type, clientExtensionResults: credential.getClientExtensionResults(), response: { authenticatorData: toBase64Url(response.authenticatorData), clientDataJSON: toBase64Url(response.clientDataJSON), signature: toBase64Url(response.signature), userHandle: response.userHandle ? toBase64Url(response.userHandle) : null } };
}

export const passkeysSupported = () => typeof window !== "undefined" && typeof window.PublicKeyCredential === "function" && !!navigator.credentials;

/** Cancelling the browser's prompt, or letting it time out, is not an error worth showing as one. */
export const cancelled = (reason: unknown) => reason instanceof DOMException && (reason.name === "NotAllowedError" || reason.name === "AbortError");
