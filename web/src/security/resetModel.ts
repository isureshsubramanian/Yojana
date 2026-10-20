export function tokenFromHash(hash: string): string | null {
  const token = new URLSearchParams(hash.startsWith("#") ? hash.slice(1) : hash).get("t");
  return token && token.length > 0 ? token : null;
}
