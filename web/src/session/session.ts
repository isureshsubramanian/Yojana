import { redirect, useRouteLoaderData } from "react-router";
import { api, ApiError } from "../api/client";
import type { Session } from "../api/types";

export const ROOT_ROUTE_ID = "root";

/** Root loader: a signed-in user with a selected tenant, or a redirect to the step that is missing. */
export async function requireSession({ request }: { request: Request }): Promise<Session> {
  const url = new URL(request.url);
  let session: Session;
  try {
    session = await api<Session>("/api/auth/me", { signal: request.signal });
  } catch (error) {
    if (error instanceof ApiError && error.status === 401) {
      const next = url.pathname === "/" ? "" : `?next=${encodeURIComponent(url.pathname + url.search)}`;
      throw redirect(`/sign-in${next}`);
    }
    throw error;
  }
  if (session.passwordChangeRequired && url.pathname !== "/change-password") {
    throw redirect("/change-password");
  }
  if (session.tenant === null && url.pathname !== "/tenants" && url.pathname !== "/change-password") {
    throw redirect("/tenants");
  }
  return session;
}

export function useSession(): Session {
  const session = useRouteLoaderData(ROOT_ROUTE_ID) as Session | undefined;
  if (!session) {
    throw new Error("useSession must be used under the root route");
  }
  return session;
}

export function initials(name: string): string {
  const parts = name.trim().split(/\s+/).filter(Boolean);
  const first = parts[0]?.[0] ?? "?";
  const last = parts.length > 1 ? parts[parts.length - 1]?.[0] ?? "" : "";
  return (first + last).toUpperCase();
}

export function roleLabel(role: string): string {
  if (role === "migration_lead") return "Migration lead";
  const words = role.replace(/_/g, " ");
  return words.charAt(0).toUpperCase() + words.slice(1);
}
