import { useEffect, useState } from "react";
import { integrationApi } from "../integration/api";
import { questionNotificationSource } from "../questions/notificationSource";
import type { NavCountSource } from "./navigation";
import { pollNotifications } from "./notificationSource";

const environment = {
  hidden: () => document.visibilityState === "hidden",
  every: (milliseconds: number, callback: () => void) => window.setInterval(callback, milliseconds),
  cancel: (handle: unknown) => window.clearInterval(handle as number),
  onVisibility: (callback: () => void) => { document.addEventListener("visibilitychange", callback); return () => document.removeEventListener("visibilitychange", callback); },
  onFocus: (callback: () => void) => { window.addEventListener("focus", callback); return () => window.removeEventListener("focus", callback); },
};

export function useNavigationCounts(enabledIntegration: boolean): Partial<Record<NavCountSource, number>> {
  const [counts, setCounts] = useState<Partial<Record<NavCountSource, number>>>({});
  useEffect(() => {
    const controller = new AbortController();
    const stop = pollNotifications(async () => {
      const reads: Array<Promise<readonly [NavCountSource, number]>> = [questionNotificationSource.load(controller.signal).then(summary => ["questions", summary.count] as const)];
      if (enabledIntegration) reads.push(integrationApi.attention(controller.signal).then(summary => ["integration_attention", summary.needsAttention] as const));
      const settled = await Promise.allSettled(reads);
      if (!controller.signal.aborted) setCounts(current => ({ ...current, ...Object.fromEntries(settled.flatMap(result => result.status === "fulfilled" ? [result.value] : [])) }));
    }, environment);
    return () => { controller.abort(); stop(); };
  }, [enabledIntegration]);
  return counts;
}
