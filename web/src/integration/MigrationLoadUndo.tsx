import { useState } from "react";
import { DataGrid, type GridColumn } from "../components";
import { ErrorNote } from "../units/common";
import { migrationApi } from "./migrationApi";
import { rollbackOutcomeLabel, undoConfirmation } from "./migrationModel";
import type { MigrationLoadSummary, RollBackMigrationLoadResponse, TouchedMigrationLoadUnit } from "./migrationTypes";

export type UndoableLoad = Pick<MigrationLoadSummary, "loadId" | "label" | "kind" | "undo">;

export function UndoLoadButton({ load, busy = false, hideWhenDisabled = false, small = true, onRequest }: {
  load: UndoableLoad;
  small?: boolean;
  busy?: boolean;
  hideWhenDisabled?: boolean;
  onRequest: (load: UndoableLoad) => void;
}) {
  const reason = busy ? "Another load action is running" : load.undo.allowed ? null : load.undo.reason ?? "This load cannot be undone";
  if (hideWhenDisabled && reason) return null;
  return <span className="migration-undo-control"><button type="button" className={`btn danger${small ? " sm" : ""}`} disabled={!!reason} onClick={() => onRequest(load)}>Undo {load.label}</button>{reason && <span className="migration-disabled-reason">{reason}</span>}</span>;
}

export function useMigrationLoadUndo(onChanged: () => void | Promise<void>) {
  const [target, setTarget] = useState<UndoableLoad | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [result, setResult] = useState<RollBackMigrationLoadResponse | null>(null);
  const request = (load: UndoableLoad) => { setTarget(load); setError(null); setResult(null); };
  const cancel = () => { if (!busy) setTarget(null); };
  const run = async () => {
    if (!target) return;
    setBusy(true); setError(null); setResult(null);
    try {
      const response = await migrationApi.rollbackLoad(target.loadId);
      setResult(response); setTarget(null); await onChanged();
    } catch (reason) { setError(reason); }
    finally { setBusy(false); }
  };
  return { target, busy, error, result, request, cancel, run };
}

export function MigrationUndoPanel({ undo }: { undo: ReturnType<typeof useMigrationLoadUndo> }) {
  const copy = undo.target ? undoConfirmation(undo.target) : null;
  return <>{undo.target && copy && <div className="note warn migration-load-confirm" role="group" aria-labelledby="undo-confirm-title"><strong id="undo-confirm-title">{copy.title}</strong><p>{copy.detail}</p><div><button type="button" className="btn sm" disabled={undo.busy} onClick={undo.cancel}>Keep the load</button><button type="button" className="btn danger sm" disabled={undo.busy} onClick={() => void undo.run()}>{undo.busy ? "Undoing…" : `Undo ${undo.target.label}`}</button></div></div>}<ErrorNote error={undo.error} />{undo.result && <RollbackResult result={undo.result} />}</>;
}

function RollbackResult({ result }: { result: RollBackMigrationLoadResponse }) {
  const refused = result.outcome === "refused_touched";
  const [items, setItems] = useState(result.touchedUnits); const [nextCursor, setNextCursor] = useState(result.touchedNextCursor);
  const [page, setPage] = useState(1); const [cursors, setCursors] = useState<(string | undefined)[]>([undefined]); const [busy, setBusy] = useState(false); const [error, setError] = useState<unknown>(null);
  const go = async (target: number) => { const cursor = target > page ? nextCursor ?? undefined : cursors[target - 1]; if (target < 1 || (target > page && !cursor)) return; setBusy(true); setError(null); try { const response = await migrationApi.touchedLoadUnits(result.loadId, cursor); setItems(response.items); setNextCursor(response.nextCursor); setPage(target); setCursors(current => { const next = current.slice(); next[target - 1] = cursor; return next; }); } catch (reason) { setError(reason); } finally { setBusy(false); } };
  const columns: GridColumn<TouchedMigrationLoadUnit>[] = [{ key: "serial", label: "Touched serial", width: 100, mono: true, render: row => row.serial }];
  return <div className={`note${refused ? " warn" : ""}`} role="status"><strong>{rollbackOutcomeLabel(result.outcome)}.</strong>&nbsp;{refused
    ? `${result.touchedUnitCount.toLocaleString()} touched unit${result.touchedUnitCount === 1 ? "" : "s"} stop the undo. The load stays in place.`
    : `${result.unitsRemoved.toLocaleString()} units, ${result.eventsRemoved.toLocaleString()} facts, ${result.entitlementsRemoved.toLocaleString()} cover rows, ${result.replacementLinksRemoved.toLocaleString()} links and ${result.certificatesRemoved.toLocaleString()} certificates removed.`}
    {refused && <div className="migration-touched-grid"><ErrorNote error={error} /><DataGrid columns={columns} page={{ rows: items, total: result.touchedUnitCount, page, pageSize: 50, continuation: nextCursor, totalIsExact: true, hasMore: !!nextCursor }} rowId={row => row.serial} onPage={target => void go(target)} loading={busy} emptyText="No touched serials were returned." viewportHeight={260} /></div>}
  </div>;
}
