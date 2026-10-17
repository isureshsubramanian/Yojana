// Load the development IT-hardware review dataset into a running Yojana host.
//
//   make demo
//   YOJANA_SEED_PASSWORD=... node tools/demo-data/it-hardware.mjs [--base http://localhost:5080]
//
// Run `make seed`, start the host with `make run`, then run `make demo`. The script uses only Node built-ins and
// public HTTP APIs. Every write carries a stable command id, so a completed or partially completed run is safe to
// repeat. It stops on the first failed request and prints that response body with the step that failed.
import { createHash } from "node:crypto";
import { readFile } from "node:fs/promises";

const args = Object.fromEntries(process.argv.slice(2).reduce((pairs, value, index, all) => {
  if (value.startsWith("--")) pairs.push([value.slice(2), all[index + 1] && !all[index + 1].startsWith("--") ? all[index + 1] : "true"]);
  return pairs;
}, []));
const base = (args.base ?? process.env.YOJANA_DEMO_BASE ?? "http://localhost:5080").replace(/\/$/, "");
const email = args.email ?? "admin@dev.yojana.local";
const password = args.password ?? process.env.YOJANA_SEED_PASSWORD ?? "";
if (!password) fail("YOJANA_SEED_PASSWORD is not set. Run `make env` and `make seed`, or pass --password.");

const catalogue = JSON.parse(await readFile(new URL("../../src/Web/Yojana.Web/DevData/reference-labels.json", import.meta.url), "utf8"));
const item = Object.fromEntries(catalogue.items.map((row) => [row.code, row]));
const party = Object.fromEntries(catalogue.parties.map((row) => [row.code, row]));
const document = Object.fromEntries(catalogue.documents.map((row) => [row.number, row]));
const legalEntityId = party["LE-ASTERRA"].id;
const serviceEntityId = party["S-ASTERRA"].id;
const warehouseId = stableUuid("location:asterra-demo-warehouse");
const affectedLotId = stableUuid("lot:voltary-ps1600-2026-08-a");
const ordinaryPsuLotId = stableUuid("lot:voltary-ps1600-2026-09-b");
const productionOrder = { type: "production_order", id: document["MO-2026-0901"].id };
const invoiceFor = (number, line = 1) => ({ type: "invoice", id: document[number].id, line });
const at = (day, hour = 9, minute = 0) => {
  const value = new Date(`${day}T00:00:00Z`);
  value.setUTCHours(hour, minute, 0, 0);
  return value.toISOString();
};

let cookie = "";
async function request(step, method, path, body) {
  let response;
  try {
    response = await fetch(base + path, {
      method,
      headers: {
        ...(body === undefined ? {} : { "content-type": "application/json" }),
        ...(cookie ? { cookie } : {}),
      },
      body: body === undefined ? undefined : JSON.stringify(body),
    });
  } catch (error) {
    throw new Error(`${step}: cannot reach ${base}${path}: ${error.message}`);
  }
  const text = await response.text();
  if (!response.ok) throw new Error(`${step}: ${method} ${path} returned ${response.status}\n${text || "<empty response>"}`);
  if (!text) return null;
  try { return JSON.parse(text); }
  catch { throw new Error(`${step}: ${method} ${path} did not return JSON\n${text}`); }
}

async function step(label, work) {
  process.stdout.write(`• ${label} ... `);
  const result = await work();
  console.log("done");
  return result;
}

function stableUuid(name) {
  const bytes = Buffer.from(createHash("sha256").update(`yojana-it-demo:${name}`).digest().subarray(0, 16));
  bytes[6] = (bytes[6] & 0x0f) | 0x40;
  bytes[8] = (bytes[8] & 0x3f) | 0x80;
  const hex = bytes.toString("hex");
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}

function fail(message) {
  console.error(`Demo data not loaded: ${message}`);
  process.exit(2);
}

function serialFor(code, sequence) {
  const itemIndex = catalogue.items.findIndex((row) => row.code === code) + 1;
  return `${String(itemIndex).padStart(2, "0")}${String(sequence).padStart(6, "0")}`;
}

const units = new Map();
const cursors = new Map();
function remember(code, rows) {
  if (!units.has(code)) units.set(code, []);
  units.get(code).push(...rows);
}
function take(code, count) {
  const start = cursors.get(code) ?? 0;
  const rows = (units.get(code) ?? []).slice(start, start + count);
  if (rows.length !== count) throw new Error(`dataset plan exhausted ${code}: wanted ${count}, found ${rows.length}`);
  cursors.set(code, start + count);
  return rows;
}

async function commission(code, count, { origin = "purchased", start = 1, lotId = null } = {}) {
  const lines = Array.from({ length: count }, (_, offset) => {
    const serial = serialFor(code, start + offset);
    return {
      identifiers: [{ type: "internal_serial", rawValue: serial }],
      clientReference: `${code}-${serial}`,
      ...(lotId ? { lotId } : {}),
    };
  });
  const result = await request(`commission ${code}`, "POST", "/api/units/commission", {
    commandId: stableUuid(`commission:${code}:${start}:${count}:${origin}:${lotId ?? "none"}`),
    itemId: item[code].id,
    origin,
    lines,
    eventTime: at("2026-09-01", 8),
    actorRef: "",
    legalEntityId,
    document: origin === "manufactured" ? productionOrder : undefined,
    locationId: warehouseId,
    locationKind: "warehouse",
    source: "demo.it-hardware",
  });
  const created = result.lines.map((line, index) => {
    if (!line.unitId) throw new Error(`commission ${code}: line ${index + 1} was ${line.outcome}: ${(line.reasons ?? []).join(", ")}`);
    return { unitId: line.unitId, serial: lines[index].identifiers[0].rawValue, code, lotId };
  });
  remember(code, created);
  return created;
}

let assemblySequence = 0;
async function assemble(parent, children) {
  assemblySequence += 1;
  return request(`assemble ${parent.code} ${parent.serial}`, "POST", "/api/units/assemble", {
    commandId: stableUuid(`assembly:${parent.unitId}`),
    parentUnitId: parent.unitId,
    installs: children.map((child, index) => ({
      childUnitId: child.unitId,
      relation: child.relation ?? "component",
      slot: child.slot ?? `${child.code}-${String(index + 1).padStart(2, "0")}`,
    })),
    eventTime: at("2026-09-03", 8, assemblySequence),
    actorRef: "",
    legalEntityId,
    document: productionOrder,
  });
}

async function release(unit, sequence) {
  return request(`release ${unit.code} ${unit.serial}`, "POST", "/api/units/release", {
    commandId: stableUuid(`release:${unit.unitId}`),
    unitId: unit.unitId,
    eventTime: at("2026-09-04", 10, sequence),
    actorRef: "",
    legalEntityId,
    releaseSignatureRef: `QA-DEMO-${unit.serial}`,
    document: productionOrder,
    revision: "2026.09",
    facilityId: warehouseId,
    testResultsJson: JSON.stringify({ result: "pass", profile: "development-demo" }),
  });
}

function policyTerms(months, serviceMode, {
  parts = 100, labour = 100, travel = 0, responseHours = null, durationBasis = "time", usageLimits = [], regions = ["IN", "EU", "UK", "CH", "US"], assurance = "assurance",
} = {}) {
  return {
    duration: { value: months, unit: "months", endOfMonth: "clamp", endInclusive: true },
    durationBasis,
    usageLimits,
    start: { trigger: "invoice", fallbacks: [], capDays: null },
    coverage: { partsPct: parts, labourPct: labour, travelPct: travel, serviceMode, responseHours, coverageHours: responseHours ? "business_hours" : null },
    exclusions: ["misuse", "unauthorised_modification", "consumables"],
    regionScope: regions,
    transferable: "yes",
    settlementFormula: "full",
    replacementPolicyCode: null,
    refurbished: [],
    doaWindowDays: 30,
    assuranceOrService: assurance,
    referenceTimeZoneRule: "utc",
    serviceContractRegulated: assurance === "service",
  };
}

async function ensurePolicy(definition) {
  const versions = await request(`read policy ${definition.code}`, "GET", `/api/warranty-policies/${encodeURIComponent(definition.code)}/versions`);
  const published = versions.find((row) => row.status === "published");
  if (published) return published.policyVersionId;
  let draft = versions.find((row) => row.status === "draft");
  if (!draft) {
    draft = await request(`create policy ${definition.code}`, "POST", "/api/warranty-policies/drafts", {
      commandId: stableUuid(`policy:${definition.code}`),
      policyCode: definition.code,
      name: definition.name,
      coverageFamily: definition.family,
      entitlementType: definition.type,
      appliesToContractsFrom: "2026-01-01",
      terms: definition.terms,
      actorRef: "",
    });
  }
  const publishedResult = await request(`publish policy ${definition.code}`, "POST", `/api/warranty-policies/versions/${draft.policyVersionId}/publish`, { actorRef: "" });
  return publishedResult.policyVersionId;
}

async function createEntitlement(unit, policyVersionId, family, owner, invoice, sequence) {
  return request(`create ${family} entitlement for ${unit.code} ${unit.serial}`, "POST", "/api/entitlements/", {
    commandId: stableUuid(`entitlement:${family}:${unit.unitId}`),
    unitId: unit.unitId,
    coverageFamily: family,
    contractDate: "2026-09-02",
    obligorEntityId: legalEntityId,
    eventTime: at("2026-09-05", 9, sequence),
    actorRef: "",
    evidence: { invoice: at("2026-09-02", 12) },
    policyVersionId,
    ownerPartyRef: owner.id,
    ownerPartyType: "customer",
    payerPartyRef: owner.id,
    payerPartyType: "customer",
    conditionGrade: "new",
    channel: "direct",
    country: "IN",
    startEvidenceRef: invoice.number,
    sourceDocument: invoiceFor(invoice.number),
    customerPoRef: `PO-${owner.code}-2026`,
    provenance: "created",
    confidence: "high",
    buyerKind: "business",
  });
}

async function submitClaim(definition) {
  const submitted = await request(`submit claim ${definition.key}`, "POST", "/api/claims/", {
    commandId: stableUuid(`claim:${definition.key}`),
    unitId: definition.unit.unitId,
    identifier: null,
    claimDate: definition.claimDate,
    claimantPartyRef: definition.customer.id,
    settlementPartyRef: (definition.settlementParty ?? definition.customer).id,
    serviceEntityId,
    obligorEntityId: legalEntityId,
    serviceCountry: definition.country ?? "IN",
    currency: "INR",
    lines: definition.lines.map((line) => ({
      kind: line.kind,
      coverageFamily: line.family ?? "base_warranty",
      requestedAmount: line.amount,
      unitId: line.unit?.unitId ?? null,
      description: line.description,
      measurements: line.measurements ?? {},
    })),
    evidence: [{ kind: "attachment", reference: `DEMO-EVIDENCE-${definition.key.toUpperCase()}`, description: definition.evidence }],
    remedy: definition.remedy ?? "repair",
    consumerChoiceEvidenceRef: `CHOICE-${definition.key.toUpperCase()}`,
    eventTime: at(definition.claimDate, 11),
    actorRef: "",
  });
  if (!definition.adjudicate) return submitted;
  return request(`adjudicate claim ${definition.key}`, "POST", `/api/claims/${submitted.claimId}/adjudicate`, {
    commandId: stableUuid(`adjudicate:${definition.key}`),
    claimId: submitted.claimId,
    eventTime: at(definition.claimDate, 14),
    actorRef: "",
    reviewedBy: null,
  });
}

async function createCampaign(definition) {
  const campaignId = stableUuid(`campaign:${definition.code}`);
  await request(`create campaign ${definition.code}`, "POST", "/api/campaigns/", {
    commandId: campaignId,
    code: definition.code,
    name: definition.name,
    coverageSource: definition.coverageSource,
    reserveTag: definition.reserveTag,
    criteria: definition.criteria,
    eventTime: at("2026-10-12", 9),
    actorRef: "",
  });
  if (definition.select) {
    await request(`select campaign ${definition.code}`, "POST", `/api/campaigns/${campaignId}/select`, {
      commandId: stableUuid(`campaign-select:${definition.code}`),
      campaignId,
      eventTime: at("2026-10-12", 10),
      legalEntityId,
      actorRef: "",
    });
  }
  return campaignId;
}

try {
  await step("check the running host", () => request("health check", "GET", "/health"));
  await step(`sign in as ${email}`, async () => {
    const response = await fetch(`${base}/api/auth/sign-in`, {
      method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify({ email, password }),
    });
    const text = await response.text();
    if (!response.ok) throw new Error(`sign in returned ${response.status}\n${text || "<empty response>"}`);
    const setCookie = response.headers.getSetCookie?.()[0] ?? response.headers.get("set-cookie");
    if (!setCookie) throw new Error("sign in succeeded but returned no session cookie");
    cookie = setCookie.split(";", 1)[0];
    const session = JSON.parse(text);
    if (!session.tenant) throw new Error("the administrator has no auto-selected tenant; seed exactly one development tenant");
  });

  await step("commission 513 serialised units in item-sized batches", async () => {
    await commission("RS-1100", 6, { origin: "manufactured", lotId: affectedLotId });
    await commission("RS-1100", 30, { origin: "manufactured", start: 7 });
    await commission("RT-1200T", 1, { origin: "manufactured" });
    await commission("RT-N1", 2, { origin: "manufactured" });
    await commission("RS-1520", 2, { origin: "manufactured" });
    await commission("RG-4800", 1, { origin: "manufactured" });
    await commission("SS-1110", 2, { origin: "manufactured" });
    await commission("SS-1112", 2, { origin: "manufactured" });
    await commission("NX-4850", 4);
    await commission("NR-220", 3);
    await commission("DW-300", 8);
    await commission("LB-14", 8);
    await commission("PF-400", 3);
    await commission("MD-64R", 172);
    await commission("ND-384", 96);
    await commission("HD-20T", 28);
    await commission("PS-1600", 12, { lotId: affectedLotId });
    await commission("PS-1600", 80, { start: 13, lotId: ordinaryPsuLotId });
    await commission("BM-2", 45);
    await commission("GX-80", 8);
  });

  const rs = units.get("RS-1100");
  const twinChassis = units.get("RT-1200T")[0];
  const twinNodes = units.get("RT-N1");
  const rs15 = units.get("RS-1520");
  const gpuServer = units.get("RG-4800")[0];
  const storage1u = units.get("SS-1110");
  const storage11u = units.get("SS-1112");
  const switches = units.get("NX-4850");
  const routers = units.get("NR-220");
  const desktops = units.get("DW-300");
  const laptops = units.get("LB-14");
  const printers = units.get("PF-400");

  await step("assemble servers, twin nodes, GPU compute and storage systems", async () => {
    for (const parent of rs) await assemble(parent, [
      ...take("MD-64R", 4).map((unit, i) => ({ ...unit, slot: `DIMM-${i + 1}` })),
      ...take("ND-384", 2).map((unit, i) => ({ ...unit, slot: `NVME-${i + 1}` })),
      ...take("PS-1600", 2).map((unit, i) => ({ ...unit, slot: `PSU-${i + 1}` })),
      { ...take("BM-2", 1)[0], slot: "BMC" },
    ]);
    for (const parent of twinNodes) await assemble(parent, [
      ...take("MD-64R", 4).map((unit, i) => ({ ...unit, slot: `DIMM-${i + 1}` })),
      ...take("ND-384", 2).map((unit, i) => ({ ...unit, slot: `NVME-${i + 1}` })),
      ...take("PS-1600", 2).map((unit, i) => ({ ...unit, slot: `PSU-${i + 1}` })),
      { ...take("BM-2", 1)[0], slot: "BMC" },
    ]);
    await assemble(twinChassis, twinNodes.map((unit, i) => ({ ...unit, relation: "kit", slot: `NODE-${i + 1}` })));
    for (const parent of rs15) await assemble(parent, [
      ...take("MD-64R", 6).map((unit, i) => ({ ...unit, slot: `DIMM-${i + 1}` })),
      ...take("ND-384", 2).map((unit, i) => ({ ...unit, slot: `NVME-${i + 1}` })),
      ...take("PS-1600", 2).map((unit, i) => ({ ...unit, slot: `PSU-${i + 1}` })),
      { ...take("BM-2", 1)[0], slot: "BMC" },
    ]);
    await assemble(gpuServer, [
      ...take("MD-64R", 8).map((unit, i) => ({ ...unit, slot: `DIMM-${i + 1}` })),
      ...take("ND-384", 4).map((unit, i) => ({ ...unit, slot: `NVME-${i + 1}` })),
      ...take("GX-80", 8).map((unit, i) => ({ ...unit, slot: `GPU-${i + 1}` })),
      ...take("PS-1600", 4).map((unit, i) => ({ ...unit, slot: `PSU-${i + 1}` })),
      { ...take("BM-2", 1)[0], slot: "BMC" },
    ]);
    for (const parent of storage1u) await assemble(parent, [
      ...take("ND-384", 6).map((unit, i) => ({ ...unit, slot: `BAY-${String(i + 1).padStart(2, "0")}` })),
      ...take("HD-20T", 6).map((unit, i) => ({ ...unit, slot: `BAY-${String(i + 7).padStart(2, "0")}` })),
      ...take("PS-1600", 2).map((unit, i) => ({ ...unit, slot: `PSU-${i + 1}` })),
      { ...take("BM-2", 1)[0], slot: "BMC" },
    ]);
    for (const parent of storage11u) await assemble(parent, [
      ...take("HD-20T", 8).map((unit, i) => ({ ...unit, slot: `BAY-${String(i + 1).padStart(2, "0")}` })),
      ...take("PS-1600", 2).map((unit, i) => ({ ...unit, slot: `PSU-${i + 1}` })),
      { ...take("BM-2", 1)[0], slot: "BMC" },
    ]);
  });

  await step("release manufactured finished goods", async () => {
    const manufactured = [...rs, ...twinNodes, twinChassis, ...rs15, gpuServer, ...storage1u, ...storage11u];
    for (const [index, unit] of manufactured.entries()) await release(unit, index);
  });

  const policies = await step("define and publish warranty, goodwill and service-contract policies", async () => {
    for (const family of [
      { familyCode: "base_warranty", name: "Base warranty", stacking: false, accountingTag: "assurance", active: true },
      { familyCode: "goodwill", name: "Goodwill cover", stacking: false, accountingTag: "assurance", active: true },
      { familyCode: "service_contract", name: "Service contract", stacking: false, accountingTag: "service", active: true },
    ]) await request(`define ${family.familyCode}`, "POST", "/api/warranty-reference/coverage-families", family);

    const definitions = [
      { code: "DEMO-SERVER-36", name: "Server 36-month on-site NBD", family: "base_warranty", type: "manufacturer", terms: policyTerms(36, "on_site", { travel: 100, responseHours: 8 }) },
      { code: "DEMO-STORAGE-60", name: "Storage 60-month on-site", family: "base_warranty", type: "manufacturer", terms: policyTerms(60, "on_site", { travel: 100, responseHours: 8 }) },
      { code: "DEMO-NETWORK-60", name: "Network 60-month return-to-base", family: "base_warranty", type: "manufacturer", terms: policyTerms(60, "return_to_base") },
      { code: "DEMO-CLIENT-12", name: "Desktop and laptop 12-month return-to-base", family: "base_warranty", type: "manufacturer", terms: policyTerms(12, "return_to_base", { regions: ["IN"] }) },
      { code: "DEMO-PRINTER-12H", name: "Printer 12-month or 2,000 print-hour cover", family: "base_warranty", type: "manufacturer", terms: policyTerms(12, "return_to_base", { durationBasis: "both", usageLimits: [{ meterType: "print_hours", limit: 2000, period: "term" }] }) },
      { code: "DEMO-ACCESSORY-12", name: "Accessory 12-month proof-of-purchase cover", family: "base_warranty", type: "dealer", terms: policyTerms(12, "return_to_base") },
      { code: "DEMO-GOODWILL-12", name: "Goodwill repair authority", family: "goodwill", type: "goodwill", terms: policyTerms(12, "return_to_base") },
      { code: "DEMO-NETWORK-CONTRACT", name: "Managed network service contract", family: "service_contract", type: "contract", terms: policyTerms(24, "on_site", { travel: 100, responseHours: 4, assurance: "service" }) },
    ];
    const result = {};
    for (const definition of definitions) result[definition.code] = await ensurePolicy(definition);
    return result;
  });

  await step("attach outbound cover to the review fleet", async () => {
    const customers = {
      hpc: party["C-NANDIRA"], hospital: party["C-VELMORA"], bank: party["C-ASHVANE"], engineering: party["C-TORRENFOLD"],
      studio: party["C-MIREVA"], isp: party["C-CLOUDMERE"],
    };
    let sequence = 0;
    const cover = async (fleet, policy, family, owner, invoice) => {
      for (const unit of fleet) await createEntitlement(unit, policies[policy], family, owner, invoice, sequence++);
    };
    await cover(rs, "DEMO-SERVER-36", "base_warranty", customers.hpc, document["IN-2026-2077"]);
    await cover(storage1u, "DEMO-STORAGE-60", "base_warranty", customers.hpc, document["IN-2026-2077"]);
    await cover(switches.slice(0, 2), "DEMO-NETWORK-60", "base_warranty", customers.hpc, document["IN-2026-2077"]);
    await cover([twinChassis, ...twinNodes, rs15[0]], "DEMO-SERVER-36", "base_warranty", customers.hospital, document["IN-2026-2077"]);
    await cover([rs15[1], gpuServer], "DEMO-SERVER-36", "base_warranty", customers.engineering, document["IN-2026-2077"]);
    await cover(storage11u, "DEMO-STORAGE-60", "base_warranty", customers.bank, document["IN-2026-2077"]);
    await cover([...switches.slice(2), ...routers], "DEMO-NETWORK-60", "base_warranty", customers.isp, document["IN-2026-2081"]);
    await cover([...desktops, ...laptops], "DEMO-CLIENT-12", "base_warranty", customers.bank, document["IN-2026-2081"]);
    await cover(printers, "DEMO-PRINTER-12H", "base_warranty", customers.studio, document["IN-2026-2088"]);
    await cover([printers[0]], "DEMO-GOODWILL-12", "goodwill", customers.studio, document["IN-2026-2088"]);
    await cover([switches[2]], "DEMO-NETWORK-CONTRACT", "service_contract", customers.isp, document["IN-2026-2081"]);

    const dimm = units.get("MD-64R")[144];
    const nvme = units.get("ND-384")[84];
    const psu = units.get("PS-1600")[76];
    await createEntitlement(dimm, policies["DEMO-SERVER-36"], "base_warranty", customers.hospital, document["IN-2026-2077"], sequence++);
    await createEntitlement(nvme, policies["DEMO-STORAGE-60"], "base_warranty", customers.hpc, document["IN-2026-2077"], sequence++);
    await createEntitlement(psu, policies["DEMO-SERVER-36"], "base_warranty", customers.engineering, document["IN-2026-2077"], sequence++);
  });

  await step("create claims across statuses, coverage sources and ageing buckets", async () => {
    const hospital = party["C-VELMORA"];
    const hpc = party["C-NANDIRA"];
    const engineering = party["C-TORRENFOLD"];
    const studio = party["C-MIREVA"];
    const isp = party["C-CLOUDMERE"];
    const bank = party["C-ASHVANE"];
    await submitClaim({
      key: "twin-dimm", unit: twinNodes[0], customer: hospital, claimDate: "2026-09-23", adjudicate: true,
      evidence: "Memory diagnostics show a repeated corrected-error threshold breach.",
      lines: [{ kind: "parts", amount: 42000, unit: units.get("MD-64R")[144], description: "Failed 64 GB registered DIMM" }],
    });
    await submitClaim({
      key: "storage-nvme", unit: storage1u[0], customer: hpc, claimDate: "2026-09-27", adjudicate: true,
      evidence: "Drive health log and storage controller fault record.",
      lines: [{ kind: "parts", amount: 138000, unit: units.get("ND-384")[84], description: "3.84 TB NVMe media failure" }],
    });
    await submitClaim({
      key: "expired-vendor-psu", unit: rs15[0], customer: engineering, claimDate: "2026-10-01", adjudicate: true,
      evidence: "Power rail test confirms failure; supplier term is recorded as expired outside the public demo API.",
      lines: [{ kind: "parts", amount: 31000, unit: units.get("PS-1600")[76], description: "1600 W PSU replacement" }],
    });
    await submitClaim({
      key: "laptop-out-of-region", unit: laptops[0], customer: bank, claimDate: "2026-10-04", country: "US", adjudicate: true,
      evidence: "Customer requested service outside the policy region.",
      lines: [{ kind: "parts", amount: 65000, description: "Main board repair" }],
    });
    await submitClaim({
      key: "printer-goodwill", unit: printers[0], customer: studio, claimDate: "2026-10-07", adjudicate: true,
      evidence: "Service manager goodwill authority after intermittent print-head fault.",
      lines: [{ kind: "parts", family: "goodwill", amount: 28000, description: "Print-head module", measurements: { print_hours: 1840 } }],
    });
    await submitClaim({
      key: "switch-contract", unit: switches[2], customer: isp, claimDate: "2026-10-09", adjudicate: true,
      evidence: "Managed-service monitoring shows repeated uplink resets.",
      lines: [{ kind: "labour", family: "service_contract", amount: 18000, description: "On-site diagnostics and firmware remediation" }],
    });
    await submitClaim({
      key: "desktop-chargeable", unit: desktops[0], customer: bank, claimDate: "2026-10-11", adjudicate: true,
      evidence: "Accidental liquid damage is outside the purchased cover.",
      lines: [{ kind: "parts", family: "accidental_damage", amount: 22500, description: "Chargeable main-board replacement" }],
    });
    await submitClaim({
      key: "client-partial", unit: laptops[1], customer: bank, claimDate: "2026-10-12", adjudicate: true,
      evidence: "Covered part plus travel requested under return-to-base terms.",
      lines: [
        { kind: "parts", amount: 12000, description: "Covered display cable" },
        { kind: "travel", amount: 4500, description: "Uncovered engineer travel" },
      ],
    });
    await submitClaim({ key: "submitted-22-days", unit: rs[8], customer: hpc, claimDate: "2026-09-25", adjudicate: false, evidence: "Awaiting thermal logs.", lines: [{ kind: "labour", amount: 16000, description: "Thermal shutdown investigation" }] });
    await submitClaim({ key: "submitted-12-days", unit: routers[0], customer: isp, claimDate: "2026-10-05", adjudicate: false, evidence: "Awaiting carrier trace.", lines: [{ kind: "parts", amount: 24000, description: "Intermittent WAN module" }] });
    await submitClaim({ key: "submitted-2-days", unit: printers[1], customer: studio, claimDate: "2026-10-15", adjudicate: false, evidence: "Print samples received; triage pending.", lines: [{ kind: "labour", amount: 9000, description: "Extrusion calibration" }] });
  });

  await step("create one selected recall and one draft goodwill campaign", async () => {
    await createCampaign({
      code: "RC-PSU-1600-26A", name: "PS-1600 vendor-lot inspection", coverageSource: "recall", reserveTag: "RECALL-PSU-26A", select: true,
      criteria: [
        { kind: "child", childItemId: item["PS-1600"].id },
        { kind: "lot", lotId: affectedLotId },
      ],
    });
    await createCampaign({
      code: "GW-BMC-RS1100-26", name: "RS-1100 management-controller firmware update", coverageSource: "goodwill", reserveTag: "GOODWILL-BMC-26", select: false,
      criteria: [
        { kind: "child", childItemId: item["BM-2"].id },
        { kind: "identifier_range", scheme: "internal", rangeStart: serialFor("RS-1100", 1), rangeEnd: serialFor("RS-1100", 36) },
      ],
    });
  });

  console.log("\nIT-hardware demo dataset is ready.");
  console.log("Loaded: 513 units, 46 assembled finished goods, 8 policies, outbound entitlements, 11 claims, and 2 campaigns.");
  console.log("Public-API gaps skipped:");
  console.log("  - receipt/shipment/invoice posting and ownership/custody changes (no public document-ingestion endpoint)");
  console.log("  - inbound vendor entitlements, automatic recovery claims, and a reduced vendor answer (they depend on receipt ingestion)");
  console.log("  - firmware attribute history and the recall's firmware-range criterion (no public unit-attribute write endpoint)");
  console.log("  - proof-of-purchase claim with no unit record (the public API cannot associate an item with its default policy)");
  console.log("  - a persisted adjudicating claim (the public adjudication command enters and completes that state atomically)");
} catch (error) {
  fail(error instanceof Error ? error.message : String(error));
}
