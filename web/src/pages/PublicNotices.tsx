import { redirect } from "react-router";
import * as z from "zod/mini";
import { apiParsed } from "../api/client";
import { BareFrame } from "../shell/Frame";
import { NoticesView } from "./ThirdPartyNotices";

// Signed-out route (sign-in page, old /notices bookmarks): someone already signed in goes to the in-app page instead,
// which keeps the menu and top bar.
export async function loader({ request }: { request: Request }) {
  try { await apiParsed("/api/auth/me", z.unknown(), { signal: request.signal }); } catch { return null; }
  return redirect("/third-party-notices");
}

export function Component() {
  return <BareFrame wide><NoticesView back={{ label: "Back to sign in", to: "/sign-in" }} /></BareFrame>;
}
