import { api } from "../api/client";
import { newCommandId, now } from "../api/units";
import type { AwaitingFilter, BillNonReturnResult, ExtendReturnDueResult, LiftReplacementHoldResult, ReplacementLinkPage } from "./types";

function query(values: Record<string, string | number | boolean | null | undefined>): string {
  const params = new URLSearchParams();
  for (const [key, value] of Object.entries(values)) if (value !== null && value !== undefined && value !== "") params.set(key, String(value));
  return `?${params.toString()}`;
}

export const replacementLinksApi = {
  list: (filter: AwaitingFilter, find: string, skip: number, take: number, signal?: AbortSignal) => api<ReplacementLinkPage>(`/api/replacement-links/${query({
    state: "awaiting_return", overdue: filter === "overdue", dueWithinDays: filter === "due_soon" ? 7 : null,
    q: find, skip, take,
  })}`, { signal }),
  extendDue: (id: string, newReturnDue: string, reason: string) => api<ExtendReturnDueResult>(`/api/replacement-links/${id}/extend-return-due`, {
    method: "POST", body: { commandId: newCommandId(), replacementLinkId: id, newReturnDue, reason, eventTime: now() },
  }),
  bill: (id: string, priceBasis: string, amount: number, billingDate: string, reason: string) => api<BillNonReturnResult>(`/api/replacement-links/${id}/bill-non-return`, {
    method: "POST", body: { commandId: newCommandId(), replacementLinkId: id, priceBasis, amount, billingDate, reason, eventTime: now() },
  }),
  liftHold: (partyRef: string, reason: string) => api<LiftReplacementHoldResult>(`/api/replacement-links/holds/${partyRef}/lift`, {
    method: "POST", body: { commandId: newCommandId(), partyRef, reason, eventTime: now() },
  }),
};
