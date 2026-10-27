// The one place the client talks HTTP. Same-origin cookies; JSON in and out; non-2xx becomes ApiError.
import * as z from "zod/mini";

export class ApiError extends Error {
  public readonly status: number;
  public readonly title: string | null;
  public readonly detail: string | null;
  public readonly rule: string | null;
  public readonly unblockingStep: string | null;
  public readonly changedBy: string | null;
  public readonly changedAt: string | null;
  public readonly reasons: readonly { code: string; message: string }[];
  public readonly openRules: readonly string[];
  public readonly blockers: readonly { kind: string; recordId: string | null; reference: string; status: string; detail: string }[];

  constructor(
    status: number,
    message: string,
    title: string | null = null,
    detail: string | null = null,
    rule: string | null = null,
    unblockingStep: string | null = null,
    changedBy: string | null = null,
    changedAt: string | null = null,
    reasons: readonly { code: string; message: string }[] = [],
    openRules: readonly string[] = [],
    blockers: readonly { kind: string; recordId: string | null; reference: string; status: string; detail: string }[] = [],
  ) {
    super(message);
    this.name = "ApiError";
    this.status = status;
    this.title = title;
    this.detail = detail;
    this.rule = rule;
    this.unblockingStep = unblockingStep;
    this.changedBy = changedBy;
    this.changedAt = changedAt;
    this.reasons = reasons;
    this.openRules = openRules;
    this.blockers = blockers;
  }
}

export async function api<T>(path: string, init?: {
  method?: "GET" | "POST" | "PUT" | "DELETE" | undefined;
  body?: unknown;
  signal?: AbortSignal | undefined;
  acceptedStatuses?: readonly number[] | undefined;
}): Promise<T> {
  const multipart = typeof FormData !== "undefined" && init?.body instanceof FormData;
  const requestBody: BodyInit | null = init?.body === undefined ? null : multipart ? init.body as FormData : JSON.stringify(init.body);
  const response = await fetch(path, {
    method: init?.method ?? "GET",
    credentials: "same-origin",
    headers: init?.body === undefined || multipart ? { accept: "application/json" } : { accept: "application/json", "content-type": "application/json" },
    body: requestBody,
    ...(init?.signal ? { signal: init.signal } : {}),
  });
  if (!response.ok && !init?.acceptedStatuses?.includes(response.status)) {
    const problem = await describe(response);
    throw new ApiError(response.status, problem.message, problem.title, problem.detail, problem.rule, problem.unblockingStep, problem.changedBy, problem.changedAt, problem.reasons, problem.openRules, problem.blockers);
  }
  if (response.status === 204) {
    return undefined as T;
  }
  return (await response.json()) as T;
}

/** Rule carried by the ApiError thrown when a 2xx response does not match its schema (ADR-0019 Q3). */
export const unexpectedResponseRule = "unexpected_response";

const unavailableProblemText: Readonly<Record<string, string>> = {
  party_name_unavailable: "Not available with this connection",
  item_name_unavailable: "Not available with this connection",
  item_policy_unavailable: "Not available with this connection",
  customer_contract_unavailable: "Not available with this connection",
  vendor_warranty_terms_unavailable: "Not available with this connection",
  warranty_certificate_unavailable: "Not available with this connection",
  time_zone_unavailable: "Not available with this connection",
  stock_quantity_unavailable: "Not available with this connection",
  stock_reclassification_unavailable: "Not available with this connection",
  stock_unit_event_unavailable: "Not available with this connection",
  serial_scheme_not_configured: "Not available with this connection",
  item_family_unavailable: "Not available with this connection",
  party_contact_unavailable: "Not available with this connection",
  production_order_unavailable: "Not available with this connection",
  ownership_source_unavailable: "Not available with this connection",
  service_document_unavailable: "Not available with this connection",
  internal_sale_document_unavailable: "Not available with this connection",
  label_printer_not_configured: "Label printing is not set up for this site",
};

type ApiInit = Parameters<typeof api>[1];

/**
 * `api` with the response checked against a zod/mini schema (ADR-0019). The type comes from the schema, so the two
 * cannot drift. Unknown fields are dropped, not refused, so the server can add a field without breaking a screen.
 * A mismatch throws an ApiError; the console names where the response differed, never a value it held.
 */
export async function apiParsed<S extends z.ZodMiniType>(path: string, schema: S, init?: ApiInit): Promise<z.infer<S>> {
  return parseResponse(path, schema, await api<unknown>(path, init));
}

/** The check `apiParsed` applies, for a response read some other way (a multipart upload, for example). */
export function parseResponse<S extends z.ZodMiniType>(path: string, schema: S, value: unknown): z.infer<S> {
  const result = z.safeParse(schema, value);
  if (result.success) return result.data;
  const where = result.error.issues.map(issue => `${issue.path.map(String).join(".") || "(root)"} ${issue.code}`).join("; ");
  console.warn(`Unexpected response from ${path.split("?")[0]}: ${where}`);
  throw new ApiError(200, "The server sent an unexpected response. Reload the page and try again.",
    unexpectedResponseRule, null, unexpectedResponseRule);
}

/**
 * A request body checked against its schema before it is sent (ADR-0019 Q4). The caller's object is sent as it is:
 * the check never removes a field, so a field the schema does not declare still reaches the server, which stays the
 * authority on every rule.
 */
export function checkedBody<S extends z.ZodMiniType>(schema: S, body: z.input<S>): z.input<S> {
  z.parse(schema, body);
  return body;
}

async function describe(response: Response): Promise<{ message: string; title: string | null; detail: string | null; rule: string | null; unblockingStep: string | null; changedBy: string | null; changedAt: string | null; reasons: readonly { code: string; message: string }[]; openRules: readonly string[]; blockers: readonly { kind: string; recordId: string | null; reference: string; status: string; detail: string }[] }> {
  const fallback = `Something went wrong (HTTP ${response.status})`;
  try {
    const text = await response.text();
    if (!text) return { message: fallback, title: null, detail: null, rule: null, unblockingStep: null, changedBy: null, changedAt: null, reasons: [], openRules: [], blockers: [] };
    try {
      const problem = JSON.parse(text) as { title?: string; detail?: string; rule?: string; unblock?: string; unblockingStep?: string; changedBy?: string; changedAt?: string; reasons?: unknown; openRules?: unknown; blockers?: unknown };
      const title = typeof problem.title === "string" ? problem.title : null;
      const detail = typeof problem.detail === "string" ? problem.detail : null;
      const rule = typeof problem.rule === "string" ? problem.rule : null;
      const reasons = Array.isArray(problem.reasons) ? problem.reasons.filter((reason): reason is { code: string; message: string } => typeof reason === "object" && reason !== null && typeof (reason as { code?: unknown }).code === "string" && typeof (reason as { message?: unknown }).message === "string") : [];
      const openRules = Array.isArray(problem.openRules) ? problem.openRules.filter((value): value is string => typeof value === "string") : [];
      const blockers = Array.isArray(problem.blockers) ? problem.blockers.filter((value): value is { kind: string; recordId: string | null; reference: string; status: string; detail: string } => typeof value === "object" && value !== null && typeof (value as { kind?: unknown }).kind === "string" && (typeof (value as { recordId?: unknown }).recordId === "string" || (value as { recordId?: unknown }).recordId === null) && typeof (value as { reference?: unknown }).reference === "string" && typeof (value as { status?: unknown }).status === "string" && typeof (value as { detail?: unknown }).detail === "string") : [];
      const mapped = rule ? unavailableProblemText[rule] ?? null : null;
      return { message: mapped ?? detail ?? title ?? rule ?? fallback, title, detail: mapped ?? detail, rule, unblockingStep: problem.unblock ?? problem.unblockingStep ?? null, changedBy: problem.changedBy ?? null, changedAt: problem.changedAt ?? null, reasons, openRules, blockers };
    } catch {
      return { message: fallback, title: null, detail: null, rule: null, unblockingStep: null, changedBy: null, changedAt: null, reasons: [], openRules: [], blockers: [] };
    }
  } catch {
    return { message: fallback, title: null, detail: null, rule: null, unblockingStep: null, changedBy: null, changedAt: null, reasons: [], openRules: [], blockers: [] };
  }
}
