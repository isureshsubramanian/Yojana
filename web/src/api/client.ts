// The one place the client talks HTTP. Same-origin cookies; JSON in and out; non-2xx becomes ApiError.
export class ApiError extends Error {
  public readonly status: number;
  public readonly title: string | null;
  public readonly detail: string | null;
  public readonly rule: string | null;
  public readonly unblockingStep: string | null;
  public readonly changedBy: string | null;
  public readonly changedAt: string | null;

  constructor(
    status: number,
    message: string,
    title: string | null = null,
    detail: string | null = null,
    rule: string | null = null,
    unblockingStep: string | null = null,
    changedBy: string | null = null,
    changedAt: string | null = null,
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
  }
}

export async function api<T>(path: string, init?: {
  method?: "GET" | "POST" | "PUT" | undefined;
  body?: unknown;
  signal?: AbortSignal | undefined;
  acceptedStatuses?: readonly number[] | undefined;
}): Promise<T> {
  const response = await fetch(path, {
    method: init?.method ?? "GET",
    credentials: "same-origin",
    headers: init?.body === undefined ? { accept: "application/json" } : { accept: "application/json", "content-type": "application/json" },
    body: init?.body === undefined ? null : JSON.stringify(init.body),
    ...(init?.signal ? { signal: init.signal } : {}),
  });
  if (!response.ok && !init?.acceptedStatuses?.includes(response.status)) {
    const problem = await describe(response);
    throw new ApiError(response.status, problem.message, problem.title, problem.detail, problem.rule, problem.unblockingStep, problem.changedBy, problem.changedAt);
  }
  if (response.status === 204) {
    return undefined as T;
  }
  return (await response.json()) as T;
}

async function describe(response: Response): Promise<{ message: string; title: string | null; detail: string | null; rule: string | null; unblockingStep: string | null; changedBy: string | null; changedAt: string | null }> {
  const fallback = `Something went wrong (HTTP ${response.status})`;
  try {
    const text = await response.text();
    if (!text) return { message: fallback, title: null, detail: null, rule: null, unblockingStep: null, changedBy: null, changedAt: null };
    try {
      const problem = JSON.parse(text) as { title?: string; detail?: string; rule?: string; unblock?: string; unblockingStep?: string; changedBy?: string; changedAt?: string };
      const title = typeof problem.title === "string" ? problem.title : null;
      const detail = typeof problem.detail === "string" ? problem.detail : null;
      const rule = typeof problem.rule === "string" ? problem.rule : null;
      return { message: detail ?? title ?? rule ?? fallback, title, detail, rule, unblockingStep: problem.unblock ?? problem.unblockingStep ?? null, changedBy: problem.changedBy ?? null, changedAt: problem.changedAt ?? null };
    } catch {
      return { message: fallback, title: null, detail: null, rule: null, unblockingStep: null, changedBy: null, changedAt: null };
    }
  } catch {
    return { message: fallback, title: null, detail: null, rule: null, unblockingStep: null, changedBy: null, changedAt: null };
  }
}
