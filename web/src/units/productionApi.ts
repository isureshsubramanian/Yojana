import { z } from "zod";
import { apiParsed, checkedBody } from "../api/client.ts";

const text = z.string();
const nullableText = z.string().nullable();
const documentRef = z.object({ type: text, id: text, line: z.number().nullable() });
const orderRow = z.object({ productionOrder: documentRef, producedItemId: nullableText, heldCount: z.number(),
  oldestIssuedAt: text, parentsReceived: z.number(), plannedQuantity: z.number().nullable(), status: text,
  continuation: nullableText });
export const inProductionOrderPage = z.object({ items: z.array(orderRow), nextCursor: nullableText });
const component = z.object({ holdId: text, unitId: text, itemId: text, serial: text, issuedAt: text,
  issue: documentRef, issueLine: z.number() });
const parent = z.object({ unitId: text, itemId: text, serial: text, receipt: documentRef });
export const inProductionDetail = z.object({ productionOrder: documentRef, producedItemId: nullableText,
  plannedQuantity: z.number().nullable(), components: z.array(component), parents: z.array(parent), status: text });
export const assignProductionComponents = z.object({ commandId: text, productionOrder: documentRef,
  parentSerial: text.min(1), componentSerials: z.array(text.min(1)).min(1), legalEntityId: z.string().optional() });
const assignmentResult = z.object({ parentUnitId: text, componentUnitIds: z.array(text),
  assemblyLinkIds: z.array(text), duplicate: z.boolean() });

export type InProductionOrder = z.infer<typeof orderRow>;
export type InProductionDetail = z.infer<typeof inProductionDetail>;

export const productionApi = {
  page: (cursor?: string, signal?: AbortSignal) => apiParsed(`/api/production-holds?take=25${cursor ? `&cursor=${encodeURIComponent(cursor)}` : ""}`, inProductionOrderPage, { signal }),
  detail: (order: { type: string; id: string }, signal?: AbortSignal) => apiParsed(`/api/production-holds/${encodeURIComponent(order.type)}/${order.id}`, inProductionDetail, { signal }),
  assign: (request: z.input<typeof assignProductionComponents>) => apiParsed("/api/production-holds/assign", assignmentResult,
    { method: "POST", body: checkedBody(assignProductionComponents, request) }),
};
