import { useEffect, useState, type ReactNode } from "react";
import { Link, useParams } from "react-router";
import { KpiStrip } from "../components/KpiStrip";
import { PresenceChips, useDocumentPresence } from "../components/Presence";
import { StatusBadge } from "../components/StatusBadge";
import { usePeople } from "../people/people";
import { Breadcrumbs, PageHeader } from "../shell/PageHeader";
import { Rail } from "../shell/Drawer";
import { openDrawer } from "../shell/drawerStore";
import { ErrorNote, Kv, Section } from "../units/common";
import { useReferenceNames } from "../corrections/ReferenceFields";
import { certificateApi } from "./api";
import { certificateEventLabel, formLabel, humanise, shortHash, statusTone } from "./format";
import type { CertificateDetail, CertificateListRow, CertificateVersionChain } from "./types";
import "./certificates.css";

export function Component() {
  const { id = "" } = useParams();
  const [certificate, setCertificate] = useState<CertificateDetail | null>(null);
  const [chain, setChain] = useState<CertificateVersionChain | null>(null);
  const [error, setError] = useState<unknown>(null);
  const presence = useDocumentPresence("certificate", id, false);
  const people = usePeople(certificate?.reprints.map((row) => row.requestedBy) ?? []);
  const names = useReferenceNames({ items: [certificate?.itemId] });
  useEffect(() => {
    const controller = new AbortController(); setError(null);
    certificateApi.detail(id, controller.signal).then(async (detail) => {
      setCertificate(detail); setChain(await certificateApi.chain(detail.unitId, controller.signal));
    }).catch((reason) => { if (!controller.signal.aborted) setError(reason); });
    return () => controller.abort();
  }, [id]);

  if (!certificate) return <main className="content" id="main" tabIndex={-1}><Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Certificates", to: "/certificates" }, { label: "Certificate" }]} /><ErrorNote error={error} /><div className="empty"><h2>{error ? "Certificate unavailable" : "Loading certificate…"}</h2></div></main>;
  const triggerSchema = objectValue(certificate.payload, "trigger", "schema");
  const trigger = triggerSchema ? certificateEventLabel(String(triggerSchema)) : humanise(certificate.supersedeReason);
  const itemName = names.itemName(certificate.itemId) ?? "Item name unavailable";
  const ownChain = certificate.documentKind === "release_record" ? chain?.releaseRecords : chain?.warrantyCertificates;
  const totalVersions = ownChain?.reduce((maximum, row) => Math.max(maximum, row.version), certificate.version) ?? certificate.version;
  const predecessor = certificate.supersedesCertificateId
    ? ownChain?.find((row) => row.certificateId === certificate.supersedesCertificateId)?.trackingNumber : null;
  const provenance = certificate.signatures.some((signature) => signature.method === "authenticated_user")
    ? `Issued by ${certificate.signatures.find((signature) => signature.method === "authenticated_user")?.printedName ?? "an authenticated user"}`
    : humanise(certificate.provenance);
  const eyebrow = `${humanise(certificate.documentKind)} · version ${certificate.version} of ${totalVersions} · issued ${certificate.issuedAtDisplay}`
    + (predecessor ? ` · supersedes ${predecessor} (${humanise(certificate.supersedeReason)})` : "");
  const openVersion = (row: CertificateListRow) => openDrawer({ eyebrow: `${humanise(row.documentKind)} · version ${row.version}`, title: row.trackingNumber, mono: true }, <>
    <Section title="Version"><Kv rows={[["Status", humanise(row.status)], ["Issued", row.issuedAtDisplay], ["Form", formLabel(row.formType, row.documentKind)], ["Provenance", provenanceLabel(row)]]} /></Section>
    <div className="act"><Link className="btn primary block" to={`/certificates/${row.certificateId}`}>Open this version</Link></div>
  </>);

  return <>
    <main className="content" id="main" tabIndex={-1}>
      <Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Certificates", to: "/certificates" }, { label: certificate.trackingNumber }]} />
      <PageHeader eyebrow={eyebrow} title={`${certificate.primarySerial} · ${itemName}`} sub={`A signed ${humanise(certificate.documentKind).toLowerCase()} whose payload and presentation snapshot never change.`}
        actions={<><PresenceChips users={presence} />{chain?.releaseRecords.length && chain.warrantyCertificates.length ? <a className="btn" href={`/api/certificates/units/${certificate.unitId}/composite`} target="_blank" rel="noreferrer">Composite print</a> : null}<Link className="btn" to={`/units/${certificate.unitId}`}>Open unit</Link></>} />
      <div className="stack">
        <KpiStrip kpis={[
          { label: "Status", value: humanise(certificate.status), detail: "current state of this version" },
          { label: "Form", value: compactFormLabel(certificate.formType, certificate.documentKind), detail: `version ${certificate.version} · ${languageName(certificate.language)}` },
          { label: "Payload hash", value: shortHash(certificate.payloadHash), detail: "SHA-256 · signatures bind to it" },
          { label: "Reprints", value: certificate.reprints.length.toLocaleString(), detail: "each is a Copy, never a version" },
        ]} />
        <div className="certificate-viewer-columns">
          <section className="card" aria-labelledby="rendered-certificate-heading"><div className="card-header"><h2 id="rendered-certificate-heading">Rendered certificate</h2><span className="meta">sandboxed · scripts disabled · paper stays white</span></div>
            <div className="card-body certificate-frame-wrap"><iframe className="certificate-frame" title={`Rendered certificate ${certificate.trackingNumber}`} sandbox="" src={`/api/certificates/${certificate.certificateId}/rendered`} /></div>
          </section>
          <div className="stack certificate-side-stack">
            <ChainCard title="Release record chain" meta="build facts only" rows={chain?.releaseRecords ?? []} currentId={certificate.certificateId} onOpen={openVersion} />
            <ChainCard title="Warranty certificate chain" meta="separate · entitlements and owner" rows={chain?.warrantyCertificates ?? []} currentId={certificate.certificateId} onOpen={openVersion} empty="No warranty certificate exists for this unit yet. A warranty change never creates a release-record version." />
            <section className="card" aria-labelledby="certificate-signatures"><div className="card-header"><h2 id="certificate-signatures">Signatures</h2><span className="meta">bound to the payload hash</span></div>
              <table className="table dense"><thead><tr><th>Signer</th><th>Meaning</th><th>When</th></tr></thead><tbody>{certificate.signatures.map((row) => <tr key={row.signatureId} tabIndex={0} onClick={() => openDrawer({ eyebrow: "Signature", title: row.printedName }, <Section title="Signature"><Kv rows={[["Printed name", row.printedName], ["Meaning", humanise(row.meaning)], ["Method", humanise(row.method)], ["Bound to", <span className="mono" key="hash">{shortHash(row.hashSigned)}</span>], ["Time", row.signedAtDisplay]]} /></Section>)}><td className="primary">{row.printedName}<span className="sub">{humanise(row.method)}</span></td><td>{humanise(row.meaning)}</td><td>{row.signedAtDisplay}</td></tr>)}</tbody></table>
            </section>
            <section className="card" aria-labelledby="certificate-reprints"><div className="card-header"><h2 id="certificate-reprints">Reprint log</h2><span className="meta">Copy access events</span></div>
              {certificate.reprints.length ? <table className="table dense"><thead><tr><th>By</th><th>Recipient</th><th>When</th></tr></thead><tbody>{certificate.reprints.map((row) => <tr key={row.reprintId} tabIndex={0} onClick={() => openDrawer({ eyebrow: "Reprint · Copy", title: people(row.requestedBy) }, <Section title="Reprint"><Kv rows={[["Requested by", people(row.requestedBy)], ["Recipient", row.recipient ?? "not recorded"], ["Version", row.version], ["Time", row.atDisplay]]} /></Section>)}><td className="primary">{people(row.requestedBy)}</td><td>{row.recipient ?? "—"}</td><td>{row.atDisplay}</td></tr>)}</tbody></table> : <div className="card-body muted">No reprints recorded.</div>}
            </section>
            <section className="card" aria-labelledby="certificate-snapshots"><div className="card-header"><h2 id="certificate-snapshots">Snapshots on this version</h2></div><div className="card-body"><Kv rows={[
              ["Issuer", certificate.issuer.name], ["Owner", certificate.owner?.name ?? "not present on this document"],
              ["Branding", certificate.branding.displayName ?? humanise(certificate.branding.profile)], ["Provenance", provenance], ["Trigger", trigger],
            ]} /></div></section>
          </div>
        </div>
      </div>
    </main>
    <Rail label="Certificate"><Section title="Integrity"><Kv rows={[["Tracking number", <span className="mono" key="tracking">{certificate.trackingNumber}</span>], ["Payload SHA-256", <span className="mono" key="payload">{shortHash(certificate.payloadHash)}</span>], ["Signatures", certificate.signatures.length], ["Certificate version", `v${certificate.version}`]]} /></Section><Section title="Rendering boundary"><p className="muted">The stored HTML is displayed in a sandboxed iframe with no script permission. The renderer also sends a content-security policy that blocks scripts and network resources.</p></Section></Rail>
  </>;
}

function ChainCard({ title, meta, rows, currentId, onOpen, empty }: { title: string; meta: string; rows: CertificateListRow[]; currentId: string; onOpen: (row: CertificateListRow) => void; empty?: string }) {
  return <section className="card"><div className="card-header"><h2>{title}</h2><span className="meta">{meta}</span></div><div className="card-body">
    {rows.length ? <ol className="certificate-chain">{[...rows].sort((a, b) => b.version - a.version).map((row) => <li key={row.certificateId} aria-current={row.certificateId === currentId} onClick={() => onOpen(row)}><span className="chain-version">{row.version}</span><span><span className="t mono">{row.trackingNumber}</span><span className="sub">{row.issuedAtDisplay} · {provenanceLabel(row)}</span></span><StatusBadge tone={statusTone(row.status)}>{humanise(row.status)}</StatusBadge></li>)}</ol> : <div className="note">{empty ?? "No versions."}</div>}
  </div></section>;
}

function objectValue(root: Record<string, unknown>, object: string, key: string): ReactNode {
  const value = root[object];
  if (typeof value !== "object" || value === null || Array.isArray(value)) return null;
  const nested = (value as Record<string, unknown>)[key];
  return typeof nested === "string" || typeof nested === "number" ? String(nested) : null;
}

function languageName(code: string): string {
  try { return new Intl.DisplayNames(undefined, { type: "language" }).of(code) ?? code; }
  catch { return code; }
}

function compactFormLabel(formType: string, documentKind: string): string {
  if (formType === "easa_form_1_style") return "EASA Form 1-style";
  if (formType === "release_8130_3_style") return "8130-3 style";
  return formLabel(formType, documentKind);
}

function provenanceLabel(row: CertificateListRow): string {
  return row.issuedBy ? `Issued by ${row.issuedBy}` : humanise(row.provenance);
}
