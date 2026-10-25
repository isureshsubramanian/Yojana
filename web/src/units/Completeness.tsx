import { useEffect, useMemo, useState } from "react";
import { Link, useNavigate, useSearchParams } from "react-router";
import { units, type CompletenessResult, type CompletenessRow } from "../api/units";
import { DataGrid, KpiStrip, ScanInput, Shortcut, StatusBadge, type GridColumn } from "../components";
import { openDrawer } from "../shell/drawerStore";
import { Rail } from "../shell/Drawer";
import { Icon } from "../shell/Icons";
import { useHandoff, useLegacyParam } from "../shell/handoff";
import { Breadcrumbs } from "../shell/PageHeader";
import { ErrorNote, Kv, Section, UnitLink } from "./common";
import { fmtDate, humanise, parseDocumentParam, shortId, splitSerials } from "./format";
import { useSession } from "../session/session";
import { useReferenceNames } from "../corrections/ReferenceFields";

const TAKE = 200;

/** As-built completeness (P1 screen 5): one row per unit, one column per expected serialised slot; the policy decides, the screen shows. */
export function Component() {
  const [params, setParams] = useSearchParams();
  const session = useSession();
  const navigate = useNavigate();
  useLegacyParam("serials", "serials");
  const serialsParam = useHandoff().serials ?? "";
  const selectionId = params.get("selection") ?? "";
  const documentParam = params.get("document") ?? "";
  const [text, setText] = useState(splitSerials(serialsParam).join(", "));
  const [result, setResult] = useState<CompletenessResult | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [facet, setFacet] = useState<"all" | "complete" | "missing" | "unexpected">("all");
  const [slotFilter, setSlotFilter] = useState<string | null>(null);
  const [page, setPage] = useState(1);
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [active, setActive] = useState<string | null>(null);
  const docRef = useMemo(() => parseDocumentParam(documentParam), [documentParam]);
  useEffect(() => { if (serialsParam) setText(splitSerials(serialsParam).join(", ")); }, [serialsParam]);

  useEffect(() => {
    const serials = splitSerials(serialsParam);
    if (serials.length === 0 && !selectionId && !docRef) { setResult(null); return; }
    const controller = new AbortController();
    setLoading(true); setError(null);
    units.completeness({ serials: serials.length ? serials : null, selectionId: selectionId || null, document: docRef, take: TAKE }, controller.signal)
      .then((r) => { setResult(r); setLoading(false); })
      .catch((e) => { if (!(e instanceof DOMException && e.name === "AbortError")) { setError(e); setLoading(false); } });
    return () => controller.abort();
  }, [serialsParam, selectionId, docRef]);

  const run = (value: string) => {
    const next = new URLSearchParams();
    const doc = parseDocumentParam(value);
    if (doc) next.set("document", value);
    else if (/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value.trim())) next.set("selection", value.trim());
    else { void navigate("/units/completeness", { state: { serials: splitSerials(value).join("\n") } }); setPage(1); return; }
    setParams(next);
    setPage(1);
  };

  const rows = useMemo(() => {
    const all = result?.page.items ?? [];
    const byFacet = facet === "complete" ? all.filter((r) => r.complete) : facet === "missing" ? all.filter((r) => r.missing > 0) : facet === "unexpected" ? all.filter((r) => r.unexpected > 0) : all;
    return slotFilter ? byFacet.filter((r) => r.slots.some((s) => s.slot === slotFilter && s.outcome !== "complete")) : byFacet;
  }, [result, facet, slotFilter]);
  const PAGE = 50;
  const grid = { rows, total: rows.length, page, pageSize: PAGE, continuation: null };
  const slots = result?.slots ?? [];
  const names = useReferenceNames({ items: rows.flatMap(row => [row.itemId, ...row.slots.map(slot => slot.capturedItemId)]) });
  const completeSerials = (result?.page.items ?? []).filter((r) => r.complete && r.serial).map((r) => r.serial!);

  const columns: GridColumn<CompletenessRow>[] = [
    { key: "serial", label: "Serial", width: slots.length ? 16 : 40, sort: { key: "serial", value: r => r.serial }, render: (r) => <UnitLink unitId={r.unitId}>{r.serial ?? shortId(r.unitId)}</UnitLink> },
    ...slots.map((slot) => ({
      key: `slot-${slot}`, label: slot, width: Math.max(10, Math.floor(66 / slots.length)), sort: { key: `slot-${slot}`, value: (r: CompletenessRow) => r.slots.find(x => x.slot === slot)?.outcome ?? "" },
      render: (r: CompletenessRow) => {
        const s = r.slots.find((x) => x.slot === slot);
        if (!s) return <span className="muted">—</span>;
        if (s.outcome === "complete") return <span className="serial">{s.capturedSerial ?? shortId(s.capturedUnitId)}</span>;
        if (s.outcome === "missing") return <span className="cell-bad" style={{ display: "block", margin: "0 -12px", padding: "0 12px" }}><StatusBadge tone="bad">Missing</StatusBadge></span>;
        return <span className="cell-warn" style={{ display: "block", margin: "0 -12px", padding: "0 12px" }}><span className="serial">{s.capturedSerial ?? "?"}</span><span className="sub">{humanise(s.outcome)}{s.capturedItemId ? ` · ${names.item(s.capturedItemId)}` : ""}</span></span>;
      },
    })),
    { key: "status", label: "Status", width: slots.length ? 18 : 42, sort: { key: "status", value: r => r.complete ? "complete" : r.unexpected > 0 && r.missing === 0 ? "unexpected" : "missing" }, render: (r) => r.complete ? <StatusBadge tone="ok">Complete</StatusBadge> : r.unexpected > 0 && r.missing === 0 ? <StatusBadge tone="warn">Unexpected{r.policy === "block" ? " · blocked" : ""}</StatusBadge> : <StatusBadge tone="bad">Missing {r.missing}{r.policy === "block" ? " · blocked" : " · warn"}</StatusBadge> },
  ];

  const rowClick = (r: CompletenessRow) => openDrawer({ eyebrow: `Selected unit · ${names.item(r.itemId)}`, title: r.serial ?? shortId(r.unitId), mono: true }, (
    <>
      <h3>Slots · expected versus captured</h3>
      {r.slots.map((s, i) => (
        <div className="job" key={`${s.slot}-${i}`}>
          <div><div className="t">{s.slot} · {s.expectedItemCode}</div><div className="s serial">{s.capturedSerial ?? (s.outcome === "missing" ? "not captured" : "—")}</div></div>
          <StatusBadge tone={s.outcome === "complete" ? "ok" : s.outcome === "missing" ? "bad" : "warn"}>{s.outcome === "complete" ? "Captured" : humanise(s.outcome)}</StatusBadge>
        </div>
      ))}
      <Kv rows={[["Policy", r.policy], ["Expected", r.expected], ["Captured", r.captured], ["Missing", r.missing], ["Unexpected", r.unexpected]]} />
      <div className="act"><Link className="btn primary block" to={`/units/${r.unitId}`}>Open unit</Link></div>
    </>
  ));

  return (
    <>
      <main className="content" id="main" tabIndex={-1}>
        <Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Units", to: "/units" }, { label: "As-built completeness" }]} />
        <div className="page-header">
          <div>
            <div className="eyebrow">Serialisation completeness · expected from the BOM, phantoms expanded · policy from the item</div>
            <h1>As-built completeness</h1>
            <p className="sub">{result ? `${result.units} units checked · ${slots.length} serialised slots expected${result.unknown.length ? ` · ${result.unknown.length} serials unknown` : ""}` : "Paste serials, a selection-set id or a document (type:id) to check which required component serials are missing."}</p>
          </div>
          <div className="actions">
            <button type="button" className="btn" onClick={() => window.print()}><Icon name="print" />Print</button>
            <Link className={`btn${completeSerials.length ? " primary" : ""}`} to="/units/release" state={completeSerials.length ? { serials: completeSerials.join("\n") } : null}>Release {completeSerials.length} complete units</Link>
          </div>
        </div>

        <div className="stack">
          {result && (
            <KpiStrip kpis={[
              { label: "Units checked", value: String(result.units), detail: "serial, list, selection or document" },
              { label: "Complete", value: String(result.complete), detail: `${slots.length} of ${slots.length} slots captured` },
              { label: "Missing serials", value: String(result.missingSerials), detail: `across ${result.missingUnits} units`, trend: result.missingSerials ? "down" : null },
              { label: "Unexpected items", value: String(result.unexpectedUnits), detail: "an item not expected in the slot" },
              { label: "Release blocked", value: String(result.page.items.filter((r) => !r.complete && r.policy === "block").length), detail: "policy block" },
            ]} label="Completeness figures" />
          )}
          <section className="card" aria-labelledby="h-matrix">
            <div className="card-header">
              <div className="toolbar" style={{ flex: 1 }}>
                <ScanInput primary value={text} onChange={setText} ariaLabel="Serial, pasted list, selection id or document" placeholder="Serial, pasted list, selection id or document type:id" hint={<Shortcut keys="enter" />} onSubmit={(s) => run(s.value)} />
                {result && ([["all", `All ${result.units}`], ["complete", `Complete ${result.complete}`], ["missing", `Missing ${result.missingUnits}`], ["unexpected", `Unexpected ${result.unexpectedUnits}`]] as const).map(([id, label]) => (
                  <button type="button" key={id} className={`chip${facet === id ? " on" : ""}`} aria-pressed={facet === id} onClick={() => { setFacet(id); setPage(1); }}>{label}</button>
                ))}
                {slots.length > 0 && <label className="chip-field"><span className="chip-k">Slot</span><select className={`chip-select${slotFilter ? " on" : ""}`} aria-label="Slot" value={slotFilter ?? ""} onChange={(e) => { setSlotFilter(e.target.value || null); setPage(1); }}><option value="">Any</option>{slots.map((s) => <option key={s} value={s}>{s} · not captured</option>)}</select></label>}
              </div>
              <span className="meta nowrap" id="h-matrix">Expected versus captured per slot</span>
            </div>
            <ErrorNote error={error} />
            <DataGrid columns={columns} page={grid} rowId={(r) => r.unitId} onPage={setPage} loading={loading} emptyText={result ? "No unit matches these filters." : "Enter serials to check."} viewportHeight={560}
              selection={{ selected, onChange: setSelected, label: (r) => r.serial ?? r.unitId }} onRowClick={(r) => { setActive(r.unitId); rowClick(r); }} activeRowId={active} describedBy="h-matrix"
              gridId="unit-completeness" userId={session.user.id} defaultSort={{ key: "serial", dir: "asc" }} defaultPageSize={PAGE}
              footer="One row per unit, one column per expected serialised slot" />
          </section>
          {result && result.unknown.length > 0 && <div className="note warn"><strong>{result.unknown.length} serials unknown:</strong>&nbsp;<span className="serial">{result.unknown.slice(0, 20).join(", ")}{result.unknown.length > 20 ? " …" : ""}</span></div>}
        </div>
      </main>
      <Rail label="Expected components">
        <Section title="Expected from BOM">
          {slots.length === 0 ? <p className="muted">Run a check to see the slots the BOM expects.</p> : slots.map((slot) => {
            const missing = (result?.page.items ?? []).filter((r) => r.slots.some((s) => s.slot === slot && s.outcome === "missing")).length;
            const code = (result?.page.items ?? []).flatMap((r) => r.slots).find((s) => s.slot === slot)?.expectedItemCode ?? "";
            return <div className="job" key={slot}><div><div className="t">{slot} · {code}</div><div className="s">tracking serial · 1 per unit</div></div><StatusBadge tone={missing ? "bad" : "ok"}>{missing ? `${missing} missing` : "all captured"}</StatusBadge></div>;
          })}
        </Section>
        <Section title="Policy">
          <Kv rows={[["asbuilt_completeness", result?.page.items[0]?.policy ?? "—"], ["Checked at", "release · sale"], ["Refusal code", <span className="serial">incomplete_asbuilt</span>], ["Effective at", fmtDate(new Date().toISOString())]]} />
        </Section>
        <Section title="Rules applied">
          <p className="muted">Expected = BOM components with tracking mode serial, effective at the date given, phantoms expanded. Missing = expected minus open assembly-link children carrying an identifier. The same query answers customer lists that need the root component part and serial per unit.</p>
        </Section>
      </Rail>
    </>
  );
}
