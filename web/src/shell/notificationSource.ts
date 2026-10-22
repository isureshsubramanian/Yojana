export interface ShellNotificationItem {
  id: string;
  title: string;
  detail: string;
  href: string;
  at: string;
}

export interface ShellNotificationSummary { count: number; items: ShellNotificationItem[] }

export interface ShellNotificationSource {
  key: string;
  load(signal?: AbortSignal): Promise<ShellNotificationSummary>;
}

export interface NotificationPollEnvironment {
  hidden(): boolean;
  every(milliseconds: number, callback: () => void): unknown;
  cancel(handle: unknown): void;
  onVisibility(callback: () => void): () => void;
  onFocus(callback: () => void): () => void;
}

export function pollNotifications(load: () => Promise<void>, environment: NotificationPollEnvironment) {
  let timer: unknown = null; let stopped = false; let pending = false; let refreshAfterPending = false;
  const refresh = () => {
    if (stopped || environment.hidden() || pending) return;
    pending = true;
    void load().catch(() => { /* the shell stays usable while offline */ }).finally(() => {
      pending = false;
      if (refreshAfterPending) { refreshAfterPending = false; refresh(); }
    });
  };
  const pause = () => { if (timer !== null) { environment.cancel(timer); timer = null; } };
  const resume = () => {
    if (stopped || environment.hidden()) return;
    if (pending) refreshAfterPending = true; else refresh();
    if (timer === null) timer = environment.every(20_000, refresh);
  };
  const visibility = () => { if (environment.hidden()) pause(); else resume(); };
  const off = [environment.onVisibility(visibility), environment.onFocus(resume)];
  resume();
  return () => { stopped = true; pause(); off.forEach(remove => remove()); };
}
