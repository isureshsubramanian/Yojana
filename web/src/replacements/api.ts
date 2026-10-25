import { apiParsed } from "../api/client.ts";
import { newCommandId, now } from "../api/units.ts";
import { billNonReturnResult, extendReturnDueResult, liftReplacementHoldResult, replacementLinkPage, type AwaitingFilter } from "./types.ts";

function query(values: Record<string, string | number | boolean | null | undefined>): string {
  const params = new URLSearchParams();
  for (const [key, value] of Object.entries(values)) if (value !== null && value !== undefined && value !== "") params.set(key, String(value));
  return `?${params.toString()}`;
}

export const replacementLinksApi = {
  list: (filter: AwaitingFilter, find: string, skip: number, take: number, signal?: AbortSignal) => apiParsed(`/api/replacement-links/${query({
    state: "awaiting_return", overdue: filter === "overdue", dueWithinDays: filter === "due_soon" ? 7 : null,
    q: find, skip, take,
  })}`, replacementLinkPage, { signal }),
  extendDue: (id: string, newReturnDue: string, reason: string) => apiParsed(`/api/replacement-links/${id}/extend-return-due`, extendReturnDueResult, {
    method: "POST", body: { commandId: newCommandId(), replacementLinkId: id, newReturnDue, reason, eventTime: now() },
  }),
  bill: (id: string, priceBasis: string, amount: number, billingDate: string, reason: string) => apiParsed(`/api/replacement-links/${id}/bill-non-return`, billNonReturnResult, {
    method: "POST", body: { commandId: newCommandId(), replacementLinkId: id, priceBasis, amount, billingDate, reason, eventTime: now() },
  }),
  liftHold: (partyRef: string, reason: string) => apiParsed(`/api/replacement-links/holds/${partyRef}/lift`, liftReplacementHoldResult, {
    method: "POST", body: { commandId: newCommandId(), partyRef, reason, eventTime: now() },
  }),
};
