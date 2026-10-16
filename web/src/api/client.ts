// The one place the client talks HTTP. Same-origin cookies; JSON in and out; non-2xx becomes ApiError.
export class ApiError extends Error {
  constructor(public readonly status: number, message: string) {
    super(message);
    this.name = "ApiError";
  }
}

export async function api<T>(path: string, init?: { method?: "GET" | "POST"; body?: unknown; signal?: AbortSignal | undefined }): Promise<T> {
  const response = await fetch(path, {
    method: init?.method ?? "GET",
    credentials: "same-origin",
    headers: init?.body === undefined ? { accept: "application/json" } : { accept: "application/json", "content-type": "application/json" },
    body: init?.body === undefined ? null : JSON.stringify(init.body),
    ...(init?.signal ? { signal: init.signal } : {}),
  });
  if (!response.ok) {
    throw new ApiError(response.status, await describe(response));
  }
  if (response.status === 204) {
    return undefined as T;
  }
  return (await response.json()) as T;
}

async function describe(response: Response): Promise<string> {
  try {
    const text = await response.text();
    if (!text) return response.statusText || `HTTP ${response.status}`;
    try {
      const problem = JSON.parse(text) as { title?: string; detail?: string };
      return problem.detail ?? problem.title ?? text;
    } catch {
      return text;
    }
  } catch {
    return `HTTP ${response.status}`;
  }
}
