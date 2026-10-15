// Fixture data for the pattern gallery, mirroring the vetted PHASE-00 mockups. Development builds only:
// the gallery is excluded from the production bundle (see routes.tsx). Parties and serials are fictional.
import type { ApprovalItem, Kpi, PageResult, PreviewResult, PreviewRow, SearchResponse, TimelineEntry, Tone } from "../components/types";

export interface UnitRow {
  id: string;
  serial: string;
  item: string;
  itemName: string;
  status: string;
  tone: Tone;
  custody: string;
  custodySub: string;
  warrantyEnd: string | null;
  lastEvent: string;
  lastEventAt: string;
}

const base: Omit<UnitRow, "id" | "serial">[] = [
  { item: "VX-220", itemName: "Vacuum controller", status: "In service", tone: "ok", custody: "Brightwater Analytics GmbH", custodySub: "Owner since 2026-09-02", warrantyEnd: "2028-09-02", lastEvent: "unit.shipped", lastEventAt: "2026-09-01 14:12" },
  { item: "VX-220", itemName: "Vacuum controller", status: "In service", tone: "ok", custody: "Brightwater Analytics GmbH", custodySub: "Owner since 2026-09-02", warrantyEnd: "2028-09-02", lastEvent: "entitlement.created", lastEventAt: "2026-09-02 08:40" },
  { item: "VX-220", itemName: "Vacuum controller", status: "In RMA", tone: "warn", custody: "Service bench · Leeds", custodySub: "Owner Brightwater Analytics GmbH", warrantyEnd: "2028-09-02", lastEvent: "rma.received", lastEventAt: "2026-10-12 09:05" },
  { item: "VX-220", itemName: "Vacuum controller", status: "In service", tone: "ok", custody: "Caldera Process Systems SA", custodySub: "Owner since 2026-08-21", warrantyEnd: "2028-08-21", lastEvent: "unit.shipped", lastEventAt: "2026-08-20 16:30" },
  { item: "PM-41", itemName: "Pressure module", status: "In stock", tone: "info", custody: "Finished goods · Leeds", custodySub: "Bin FG-04-12", warrantyEnd: null, lastEvent: "unit.commissioned", lastEventAt: "2026-10-14 11:48" },
  { item: "GW-7 LTE", itemName: "Gateway (IMEI)", status: "In service", tone: "ok", custody: "Harbourline Logistics plc", custodySub: "Owner since 2025-03-11", warrantyEnd: "2027-03-11", lastEvent: "usage.reading", lastEventAt: "2026-10-15 06:00" },
  { item: "GW-7 LTE", itemName: "Gateway (IMEI)", status: "Refurbished", tone: "neutral", custody: "Finished goods · Leeds", custodySub: "Grade B · refurb 2026-10-09", warrantyEnd: null, lastEvent: "refurbishment.released", lastEventAt: "2026-10-09 15:22" },
  { item: "VX-220", itemName: "Vacuum controller", status: "Awaiting serial", tone: "bad", custody: "Receiving · Leeds", custodySub: "PO-2026-00841 line 3", warrantyEnd: null, lastEvent: "unit.commissioned", lastEventAt: "2026-10-13 10:02" },
  { item: "VX-220", itemName: "Vacuum controller", status: "Loaner out", tone: "warn", custody: "Brightwater Analytics GmbH", custodySub: "Loaner since 2026-10-12", warrantyEnd: "2028-07-30", lastEvent: "service.loaner_issued", lastEventAt: "2026-10-12 10:40" },
];

export const UNITS_TOTAL = 1_284_317;

export function unitsPage(page: number, pageSize = 50): PageResult<UnitRow> {
  const rows: UnitRow[] = [];
  for (let i = 0; i < pageSize; i++) {
    const n = (page - 1) * pageSize + i;
    const t = base[n % base.length]!;
    const serial = t.item === "GW-7 LTE" ? `3569380356${(43809 + n).toString().padStart(5, "0")}` : `2506-${(493445 + n).toString()}`;
    rows.push({ id: `u-${n}`, serial, ...t });
  }
  return { rows, total: UNITS_TOTAL, page, pageSize, continuation: page * pageSize < UNITS_TOTAL ? `p${page + 1}` : null };
}

export const unitKpis: Kpi[] = [
  { label: "In stock", value: "212,448", detail: "41 locations" },
  { label: "In service", value: "1,284,317", detail: "+4,120 this month", trend: "up" },
  { label: "Awaiting vendor serial", value: "1,212", detail: "3 orders overdue", trend: "down" },
  { label: "In RMA", value: "412", detail: "17 past window" },
  { label: "Projection lag", value: "0.3 s", detail: "Within 1 s", trend: "up" },
];

export const timeline: TimelineEntry[] = [
  { kind: "episode", label: "Ownership · Brightwater Analytics GmbH · from 2026-09-02" },
  { kind: "event", title: "Warranty created", code: "entitlement.created", meta: "2026-09-02 08:40 · base · 24 months · from delivery DN-2026-018432", emphasis: "key" },
  { kind: "event", title: "Ownership opened", code: "ownership.opened", meta: "2026-09-02 08:40 · Brightwater Analytics GmbH · invoice INV-2026-11207", emphasis: "key" },
  { kind: "event", title: "Shipped", code: "unit.shipped", meta: "2026-09-01 14:12 · Leeds FG → carrier · delivery DN-2026-018432 line 4", recordedAt: "2026-09-01 14:12:07" },
  { kind: "episode", label: "Refurbishment · RWO-2026-0412" },
  { kind: "event", title: "Refurbishment released", code: "refurbishment.released", meta: "2026-08-14 16:05 · grade A · 2 components replaced · tests passed 6/6", emphasis: "key" },
  { kind: "event", title: "Graded", code: "unit.graded", meta: "2026-08-02 10:30 · grade B on intake · sanitised" },
  { kind: "event", title: "Return received", code: "rma.received", meta: "2026-07-29 09:12 · RMA-2026-1183 · reported fault: no vacuum", emphasis: "bad" },
  { kind: "episode", label: "Ownership · Oakridge Instruments Ltd · 2025-03-14 to 2026-07-29" },
  { kind: "event", title: "Ownership closed", code: "ownership.closed", meta: "2026-07-29 09:12 · reason return · replaced by 2506-498770" },
  { kind: "event", title: "Warranty transferred", code: "entitlement.transferred", meta: "2026-07-29 09:12 · to 2506-498770 · policy carry-over", emphasis: "key" },
  { kind: "event", title: "Shipped", code: "unit.shipped", meta: "2025-03-13 11:50 · delivery DN-2025-004410 line 1" },
  { kind: "episode", label: "Build · WO-2025-01772" },
  { kind: "event", title: "Released", code: "unit.released", meta: "2025-03-10 15:20 · release record v1 · signed by Q. Mahler" },
  { kind: "event", title: "Commissioned", code: "unit.commissioned", meta: "2025-03-06 08:02 · lot L-2503-07 · serial scheme VX" },
];

export const approvals: ApprovalItem[] = [
  { id: "a1", command: "AmendEntitlement", initiator: "P. Lindqvist", requestedAt: "2026-10-13", selection: "DN-2026-018432", selectionSub: "Delivery", selectionMono: false, units: 214, status: "Awaiting approval", tone: "warn", href: "/patterns/form" },
  { id: "a2", command: "SwapIdentifiers", initiator: "M. Adeyemi", requestedAt: "2026-10-14", selection: "2506-493445 ⇄ 2506-493446", selectionSub: null, selectionMono: true, units: 2, status: "Awaiting approval", tone: "warn", href: "/patterns/form" },
  { id: "a3", command: "CorrectDocumentSerials", initiator: "S. Brenner", requestedAt: "2026-10-14", selection: "DN-2026-019901", selectionSub: "Delivery", selectionMono: false, units: 50, status: "Second approver", tone: "warn", href: "/patterns/form" },
  { id: "a4", command: "BackfillEntitlements", initiator: "R. Okafor", requestedAt: "2026-10-15", selection: "VX-220", selectionSub: "Item · 2024 H1", selectionMono: false, units: 1380, status: "Preview ready", tone: "info", href: "/patterns/form" },
  { id: "a5", command: "CorrectItem", initiator: "A. Varga", requestedAt: "2026-10-15", selection: "2507-110882", selectionSub: "Unit", selectionMono: true, units: 1, status: "Finance review", tone: "warn", href: "/patterns/form" },
];

const outcomes: Pick<PreviewRow, "current" | "next" | "outcome" | "reason">[] = [
  { current: "2028-09-02", next: "2028-10-02", outcome: "change", reason: null },
  { current: "2028-09-02", next: "2028-10-02", outcome: "change", reason: null },
  { current: "2028-09-02", next: null, outcome: "block", reason: "open claim CL-2026-0410" },
  { current: "2028-09-02", next: "2028-10-02", outcome: "change", reason: "certificate v2" },
  { current: "2028-10-02", next: null, outcome: "skip", reason: "already at target" },
  { current: "2028-09-02", next: "2028-10-02", outcome: "change", reason: null },
  { current: null, next: null, outcome: "skip", reason: "no live base entitlement" },
  { current: "2028-09-02", next: "2028-10-02", outcome: "change", reason: null },
  { current: "2028-09-02", next: "2028-10-02", outcome: "change", reason: null },
  { current: "2028-09-02", next: null, outcome: "block", reason: "owned by another party" },
];

export const PREVIEW_TOTAL = 214;

/** What a command's preview endpoint would return for a find query, facet and page: the server does the filtering. */
export function previewPage(query: string, outcome: PreviewRow["outcome"] | null, page: number, pageSize = 50): PreviewResult {
  const all: PreviewRow[] = [];
  for (let n = 0; n < PREVIEW_TOTAL; n++) {
    const o = outcomes[n % outcomes.length]!;
    all.push({ id: `p-${n}`, serial: `2506-${493445 + n}`, ...o });
  }
  const summary = { resolved: PREVIEW_TOTAL, change: all.filter((r) => r.outcome === "change").length, skip: all.filter((r) => r.outcome === "skip").length, block: all.filter((r) => r.outcome === "block").length };
  const q = query.trim();
  const filtered = all.filter((r) => (!outcome || r.outcome === outcome) && (!q || r.serial.startsWith(q) || r.serial.includes(q)));
  const start = (page - 1) * pageSize;
  return { rows: filtered.slice(start, start + pageSize), total: filtered.length, page, pageSize, continuation: null, summary, matches: q ? filtered.length : filtered.length };
}

export interface DocumentSerialRow { id: string; serial: string; line: number; item: string; status: string; tone: Tone; owner: string; ownerSub: string; note: string | null }

export const DOCUMENT_TOTAL = 1000;

export function documentPage(query: string, facet: string | null, page: number, pageSize = 50): PageResult<DocumentSerialRow> & { matches: number } {
  const all: DocumentSerialRow[] = [];
  for (let n = 0; n < DOCUMENT_TOTAL; n++) {
    const line = Math.floor(n / 250) + 1;
    const exception = n % 97 === 48;
    const rma = n % 211 === 2;
    all.push({
      id: `d-${n}`,
      serial: line === 3 ? `2507-${110000 + n}` : `2506-${492700 + n}`,
      line,
      item: line === 3 ? "PM-41" : "VX-220",
      status: exception ? "Owner mismatch" : rma ? "In RMA" : "In service",
      tone: exception ? "bad" : rma ? "warn" : "ok",
      owner: exception ? "Caldera Process Systems SA" : rma ? "Service bench · Leeds" : "Brightwater Analytics GmbH",
      ownerSub: exception ? "Shipped on DN-2026-018432, owned via DN-2026-018440" : rma ? "Owner Brightwater Analytics GmbH" : "Customer site · Lab 2",
      note: exception ? "Candidate for CorrectDocumentSerials" : rma ? "RMA-2026-1183 · no vacuum" : null,
    });
  }
  const q = query.trim();
  const filtered = all.filter((r) => (!q || r.serial.startsWith(q)) && (facet === null || (facet === "exceptions" ? r.tone !== "ok" : facet.startsWith("line-") ? r.line === Number(facet.slice(5)) : true)));
  const start = (page - 1) * pageSize;
  return { rows: filtered.slice(start, start + pageSize), total: filtered.length, page, pageSize, continuation: null, matches: filtered.length };
}

/** A ranked response for the query "493445", as the search endpoint will return it. */
export function searchFixture(query: string): SearchResponse {
  const q = query.trim();
  const mark = (title: string) => { const i = title.indexOf(q); return i >= 0 ? [{ start: i, length: q.length }] : []; };
  const unitMeta = {
    eyebrow: "Unit · VX-220", title: "2506-493445", titleMono: true, subtitle: "Vacuum controller · In service",
    fields: [
      { label: "Owner", value: "Brightwater Analytics" }, { label: "Owner since", value: "2026-09-02" }, { label: "Custody", value: "Customer site" },
      { label: "Warranty end", value: "2028-09-02" }, { label: "Condition", value: "Grade A · refurbished" }, { label: "Open work", value: null },
      { label: "Last event", value: "unit.shipped · 09-01" }, { label: "Certificates", value: "Release v2 · Warranty v1" },
    ],
    actions: [{ label: "Open unit", href: "/patterns/detail", primary: true }, { label: "Something is wrong…", href: "/patterns/form" }],
    note: "Fields shown here are the tenant's search metadata profile for units (Settings › Search › Metadata). The profile respects disclosure tiers, so a portal user sees fewer fields.",
  };
  const docMeta = { eyebrow: "Delivery · from CX01", title: "DN-2026-018432", titleMono: false, subtitle: "Brightwater Analytics GmbH · 2026-09-01", fields: [{ label: "Serials", value: "1,000 on 4 lines" }, { label: "Sales order", value: "SO-2026-7781" }, { label: "Invoice", value: "INV-2026-11207" }, { label: "POD", value: "2026-10-02" }], actions: [{ label: "Open document", href: "/patterns/document", primary: true }], note: null };
  return {
    query: q,
    tookMs: 6,
    scopes: [{ id: "units", label: "Units", count: 4 }, { id: "documents", label: "Documents", count: 2 }, { id: "entitlements", label: "Entitlements", count: 1 }, { id: "rmas", label: "RMAs", count: 1 }, { id: "parties", label: "Parties", count: 1 }, { id: "certificates", label: "Certificates", count: 0 }],
    openDirectly: null,
    groups: [
      { label: "Units", results: [
        { id: "r1", type: "unit", href: "/patterns/detail", title: "2506-493445", titleMono: true, matches: mark("2506-493445"), subtitle: "VX-220 Vacuum controller · In service · Brightwater Analytics GmbH", relevance: 100, reason: "exact match · internal serial", metadata: unitMeta },
        { id: "r2", type: "unit", href: "/patterns/detail", title: "(01)09506000134352(21)2506493445", titleMono: true, matches: mark("(01)09506000134352(21)2506493445"), subtitle: "GS1 SGTIN of unit 2506-493445 · same unit", relevance: 96, reason: "contained in · SGTIN", metadata: unitMeta },
        { id: "r3", type: "unit", href: "/patterns/detail", title: "2506-493445-R", titleMono: true, matches: mark("2506-493445-R"), subtitle: "VX-220 · Refurbished · in stock Leeds FG-02 · replaced 2506-493445 lineage", relevance: 71, reason: "prefix match · customer serial", metadata: { ...unitMeta, title: "2506-493445-R", subtitle: "Vacuum controller · Refurbished" } },
        { id: "r4", type: "unit", href: "/patterns/detail", title: "2506-493456", titleMono: true, matches: [{ start: 5, length: 4 }], subtitle: "VX-220 · In service · Caldera Process Systems SA", relevance: 38, reason: "near match · 1 edit", metadata: { ...unitMeta, title: "2506-493456", fields: unitMeta.fields.map((f) => (f.label === "Owner" ? { ...f, value: "Caldera Process Systems" } : f)) } },
      ] },
      { label: "Documents", results: [
        { id: "r5", type: "document", href: "/patterns/document", title: "Delivery DN-2026-018432", titleMono: false, matches: [], subtitle: `1,000 serials · Brightwater Analytics GmbH · 2026-09-01 · contains 2506-${q} on line 4`, relevance: 84, reason: "document lists the serial", metadata: docMeta },
        { id: "r6", type: "document", href: "/patterns/document", title: "Return RT-2026-00212", titleMono: false, matches: [], subtitle: `1 serial · Oakridge Instruments Ltd · 2026-07-29 · contains 2506-${q}`, relevance: 80, reason: "document lists the serial", metadata: { ...docMeta, eyebrow: "Return · from CX01", title: "RT-2026-00212", subtitle: "Oakridge Instruments Ltd · 2026-07-29", fields: [{ label: "Serials", value: "1" }, { label: "RMA", value: "RMA-2026-1183" }] } },
      ] },
      { label: "Entitlements, RMAs and parties", results: [
        { id: "r7", type: "entitlement", href: "/patterns/detail", title: "Base warranty · 2506-493445 · to 2028-09-02", titleMono: false, matches: mark("Base warranty · 2506-493445 · to 2028-09-02"), subtitle: "Live · provenance sale · delivery DN-2026-018432", relevance: 78, reason: "attached to the unit", metadata: { eyebrow: "Entitlement · base", title: "2506-493445", titleMono: true, subtitle: "2026-09-02 → 2028-09-02", fields: [{ label: "Status", value: "Live" }, { label: "Provenance", value: "sale" }, { label: "Payer", value: "Seller" }], actions: [{ label: "Open unit", href: "/patterns/detail", primary: true }], note: null } },
        { id: "r8", type: "rma", href: "/patterns/detail", title: "RMA-2026-1183", titleMono: false, matches: [], subtitle: "Closed 2026-08-14 · reported fault: no vacuum · unit 2506-493445", relevance: 74, reason: "attached to the unit", metadata: { eyebrow: "RMA", title: "RMA-2026-1183", titleMono: false, subtitle: "Closed 2026-08-14", fields: [{ label: "Fault", value: "no vacuum" }, { label: "Outcome", value: "Refurbished, grade A" }], actions: [], note: null } },
        { id: "r9", type: "party", href: "/patterns/list", title: "Brightwater Analytics GmbH", titleMono: false, matches: [], subtitle: "Customer · 38,410 units owned · current owner of 2506-493445", relevance: 52, reason: "owner of a matching unit", metadata: { eyebrow: "Party · customer", title: "Brightwater Analytics GmbH", titleMono: false, subtitle: "Freiburg", fields: [{ label: "Units owned", value: "38,410" }, { label: "Live entitlements", value: "36,902" }], actions: [{ label: "Installed base", href: "/patterns/list", primary: true }], note: null } },
      ] },
    ],
  };
}
