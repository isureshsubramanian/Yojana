// The one place the client talks HTTP. Same-origin cookies; JSON in and out; non-2xx becomes ApiError.
export class ApiError extends Error {
  constructor(
    public readonly status: number,
    message: string,
    public readonly rule: string | null = null,
    public readonly unblockingStep: string | null = null,
  ) {
    super(message);
    this.name = "ApiError";
  }
}

export async function api<T>(path: string, init?: { method?: "GET" | "POST" | "PUT"; body?: unknown; signal?: AbortSignal | undefined }): Promise<T> {
  const response = await fetch(path, {
    method: init?.method ?? "GET",
    credentials: "same-origin",
    headers: init?.body === undefined ? { accept: "application/json" } : { accept: "application/json", "content-type": "application/json" },
    body: init?.body === undefined ? null : JSON.stringify(init.body),
    ...(init?.signal ? { signal: init.signal } : {}),
  });
  if (!response.ok) {
    const problem = await describe(response);
    throw new ApiError(response.status, problem.message, problem.rule, problem.unblockingStep);
  }
  if (response.status === 204) {
    return undefined as T;
  }
  return (await response.json()) as T;
}

async function describe(response: Response): Promise<{ message: string; rule: string | null; unblockingStep: string | null }> {
  try {
    const text = await response.text();
    if (!text) return { message: response.statusText || `HTTP ${response.status}`, rule: null, unblockingStep: null };
    try {
      const problem = JSON.parse(text) as { title?: string; detail?: string; rule?: string; unblockingStep?: string };
      return { message: problem.detail ?? problem.title ?? text, rule: problem.rule ?? problem.title ?? null, unblockingStep: problem.unblockingStep ?? null };
    } catch {
      return { message: text, rule: null, unblockingStep: null };
    }
  } catch {
    return { message: `HTTP ${response.status}`, rule: null, unblockingStep: null };
  }
}
