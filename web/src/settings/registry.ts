import type { ComponentType } from "react";
import { claimsApi } from "../claims/api";
import { p7Api } from "../p7/api";
import { refurbishmentsApi } from "../refurbishments/api";
import { identityTitle } from "../refurbishments/format";
import { recoveryApi } from "../recovery/api";
import { secondStepApi } from "../security/secondStepApi";
import type { IconName } from "../shell/Icons";
import { certificateDateTimeExample } from "./certificateDateFormats";
import { certificateSettingsApi, type ReleaseRecordIssuePoint } from "./certificateSettingsApi";
import { companyApi } from "./companyApi";
import { importFileSettingsApi } from "./importFileSettingsApi";
import { numberingApi } from "./numberingApi";
import { timeZoneSettingsApi } from "./timeZoneSettingsApi";
import { visibleSettings as visibleSettingRecords } from "./registryModel";

export type SettingsGroup = "Organisation" | "Documents" | "Service operations" | "Data and knowledge";
type SettingsModule = Promise<{ Component: ComponentType }>;

export interface SettingsRegistryEntry {
  group: SettingsGroup;
  name: string;
  description: string;
  path: `/settings/${string}`;
  icon: IconName;
  roles: string[];
  currentValue?: (signal: AbortSignal) => Promise<string>;
  route: () => SettingsModule;
}

const administrator = ["tenant_administrator"];
const issuePoint: Record<ReleaseRecordIssuePoint, string> = {
  at_build_release: "Issued at build release",
  at_first_shipment: "Issued at first shipment",
  on_request: "Issued on request",
};
const riskRules = ["recentCoverPurchase", "repeatUnitClaims", "repeatFault", "claimantRate", "channelMismatch", "recentOwnershipChange", "partNotReturned", "duplicateOpenClaim"] as const;

export const settingsRegistry: SettingsRegistryEntry[] = [
  { group: "Organisation", name: "Company", description: "Footer line, sender name and reply-to on every e-mail", path: "/settings/company", icon: "home", roles: administrator,
    currentValue: signal => companyApi.mail(signal).then(value => `${value.senderName} · ${value.replyTo || "No reply-to address"}`), route: () => import("./Company") },
  { group: "Organisation", name: "Users", description: "Staff memberships, invitations and tenant roles", path: "/settings/users", icon: "users", roles: administrator, route: () => import("./Users") },
  { group: "Organisation", name: "Sign-in policy", description: "Password recovery, invitations and second-step rules", path: "/settings/sign-in-policy", icon: "key", roles: administrator,
    currentValue: signal => secondStepApi.settings(signal).then(value => { const count = value.roles.filter(role => role.mode === "required").length; return `Authenticator required for ${count} ${count === 1 ? "role" : "roles"}`; }), route: () => import("./SignInPolicy") },
  { group: "Organisation", name: "Security events", description: "Sign-ins, lockouts and administrator security actions", path: "/settings/security-events", icon: "shield", roles: administrator, route: () => import("./SecurityEvents") },
  { group: "Organisation", name: "Time zone", description: "Document time plus issuing-entity overrides", path: "/settings/time-zone", icon: "globe", roles: administrator,
    currentValue: signal => timeZoneSettingsApi.get(signal).then(value => `${value.tenantTimeZone ?? "UTC"} · ${value.legalEntityOverrides.length} entity ${value.legalEntityOverrides.length === 1 ? "override" : "overrides"}`), route: () => import("./TimeZoneSettings") },
  { group: "Organisation", name: "Currencies", description: "Working and accepted currencies", path: "/settings/currencies", icon: "coins", roles: administrator,
    currentValue: signal => claimsApi.currency(signal).then(value => `${value.currency ?? "Not set"} working · ${value.accepted.length ? value.accepted.join(", ") : "no additional"} accepted`), route: () => import("./Currencies") },

  { group: "Documents", name: "Document numbering", description: "Series modes and gap behaviour per document", path: "/settings/numbering", icon: "range", roles: administrator,
    currentValue: signal => numberingApi.settings(signal).then(value => { const sequence = value.series.filter(row => row.mode === "sequence").length; return `${value.series.length} series · ${sequence === value.series.length ? "all sequence" : `${sequence} sequence`}`; }), route: () => import("./Numbering") },
  { group: "Documents", name: "Certificates", description: "Issue point and date and time presentation", path: "/settings/certificates", icon: "cert", roles: administrator,
    currentValue: async signal => { const [release, formats, zone] = await Promise.all([certificateSettingsApi.get(signal), certificateSettingsApi.getDateFormats(signal), timeZoneSettingsApi.get(signal)]); const format = formats.languages[0]; return format ? `${issuePoint[release.defaultIssuePoint]} · ${certificateDateTimeExample(format.language, format.datePattern, format.timePattern, zone.tenantTimeZone ?? "UTC")}` : issuePoint[release.defaultIssuePoint]; }, route: () => import("./CertificateSettings") },
  { group: "Documents", name: "Labels", description: "Label printers, stock and default templates", path: "/settings/labels", icon: "label", roles: administrator, route: () => import("./LabelSettings") },
  { group: "Documents", name: "Disclosure tiers", description: "What public and owner lookups may show", path: "/settings/disclosure", icon: "eye", roles: administrator,
    currentValue: signal => p7Api.disclosure(signal).then(value => `Public lookup ${value.publicTierEnabled ? "enabled" : "disabled"} · ${value.fields.filter(field => field.enabled && field.tier === "public").length} public fields`), route: () => import("../p7/Disclosure") },
  { group: "Documents", name: "Jurisdiction rules", description: "Country rules for warranty, repair and labelling", path: "/settings/jurisdictions", icon: "globe", roles: administrator, route: () => import("../jurisdictions/Jurisdictions") },

  { group: "Service operations", name: "Claims risk rules", description: "Second-review checks, thresholds and reviewer role", path: "/settings/claim-risk", icon: "shield", roles: administrator,
    currentValue: signal => claimsApi.riskRules(signal).then(value => `${riskRules.filter(rule => value[rule].enabled).length} checks on`), route: () => import("./ClaimRiskRules") },
  { group: "Service operations", name: "Supplier recovery", description: "Claim lines and vendor answer timing", path: "/settings/recovery", icon: "recover", roles: administrator,
    currentValue: signal => recoveryApi.policy(signal).then(value => `Vendor answer due in ${value.vendorAnswerDueDays} days`), route: () => import("./SupplierRecovery") },
  { group: "Service operations", name: "Refurbishment policy", description: "Identity outcomes and cycle limits", path: "/settings/refurbishment", icon: "cycle", roles: administrator,
    currentValue: signal => refurbishmentsApi.policy(signal).then(value => `${identityTitle(value.defaultIdentity)} · at most ${value.defaultMaxCycles} cycles`), route: () => import("./RefurbishmentPolicy") },
  { group: "Service operations", name: "Workbench choices", description: "Correction choices and who may commit them", path: "/settings/workbench-choices", icon: "list", roles: administrator, route: () => import("../corrections/WorkbenchChoices") },

  { group: "Data and knowledge", name: "Import files", description: "Active storage and long-term archive", path: "/settings/import-files", icon: "db", roles: administrator,
    currentValue: signal => importFileSettingsApi.get(signal).then(value => `Archive after ${value.archiveAge} ${value.archiveAgeUnit} · ${value.archiveStores.length} stores`), route: () => import("./ImportFileSettings") },
  { group: "Data and knowledge", name: "Knowledge screen changes", description: "Mark a changed screen so its knowledge entries are reviewed", path: "/settings/knowledge-screen-changes", icon: "book", roles: administrator, route: () => import("./KnowledgeScreenChangesPage") },
];

export function visibleSettings(roles: readonly string[]): SettingsRegistryEntry[] {
  return visibleSettingRecords(settingsRegistry, roles);
}
