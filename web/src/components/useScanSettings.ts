import { useEffect, useState } from "react";
import { useSession } from "../session/session";
import { scanningApi } from "../settings/scanningApi";
import { DEFAULT_SCANNING_POLICY, ScanSettingsSessionCache, type ScanningPolicy } from "./scan";

const cache = new ScanSettingsSessionCache();

export function loadScanSettingsOnce(sessionKey: string, read: () => Promise<ScanningPolicy> = scanningApi.get): Promise<ScanningPolicy> {
  return cache.load(sessionKey, read);
}

export function clearScanSettingsReads(): void { cache.clear(); }

/** All scan boxes in one signed-in tenant session share one settings read. */
export function useScanSettings(): ScanningPolicy {
  const session = useSession();
  const sessionKey = `${session.user.id}:${session.tenant?.code ?? "no-tenant"}`;
  const [settings, setSettings] = useState(DEFAULT_SCANNING_POLICY);
  useEffect(() => {
    let current = true;
    void loadScanSettingsOnce(sessionKey).then(value => { if (current) setSettings(value); });
    return () => { current = false; };
  }, [sessionKey]);
  return settings;
}
