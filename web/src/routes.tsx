import { createBrowserRouter, type RouteObject } from "react-router";
import { ErrorPage } from "./pages/ErrorPage";
import { ROOT_ROUTE_ID, requireSession } from "./session/session";
import { Frame } from "./shell/Frame";
import { navigation } from "./shell/navigation";

// Every route is lazy (per-route code splitting, MOD-02). Module placeholders come from the navigation
// registry, so a later phase replaces one line here with its own lazy route and touches nothing else.
const placeholders: RouteObject[] = navigation
  .filter((e) => e.placeholder)
  .map((e) => ({ path: e.path.slice(1) + "/*", lazy: () => import("./pages/Placeholder") }));

// The pattern gallery (shared components on fixture data) exists in development builds only; the production
// define removes this branch and its chunks.
const patterns: RouteObject[] = process.env.NODE_ENV === "production" ? [] : [
  { path: "patterns", lazy: () => import("./patterns/PatternsIndex") },
  { path: "patterns/list", lazy: () => import("./patterns/ListPattern") },
  { path: "patterns/detail", lazy: () => import("./patterns/DetailPattern") },
  { path: "patterns/form", lazy: () => import("./patterns/FormPattern") },
  { path: "patterns/document", lazy: () => import("./patterns/DocumentPattern") },
];

export const router = createBrowserRouter([
  { path: "/sign-in", lazy: () => import("./pages/SignIn"), errorElement: <ErrorPage /> },
  { path: "/forgot-password", lazy: () => import("./pages/ForgotPassword"), errorElement: <ErrorPage /> },
  { path: "/reset-password", lazy: () => import("./pages/ResetPassword"), errorElement: <ErrorPage /> },
  { path: "/accept-invitation", lazy: () => import("./pages/AcceptInvitation"), errorElement: <ErrorPage /> },
  { path: "/public/:tenantCode/lookup", lazy: () => import("./p7/PublicLookup"), errorElement: <ErrorPage /> },
  {
    id: ROOT_ROUTE_ID,
    path: "/",
    loader: requireSession,
    errorElement: <ErrorPage />,
    children: [
      { path: "tenants", lazy: () => import("./pages/ChooseTenant") },
      { path: "change-password", lazy: () => import("./pages/ChangePassword") },
      {
        path: "portal",
        lazy: () => import("./p7/PortalLayout"),
        errorElement: <ErrorPage />,
        children: [
          { index: true, lazy: () => import("./p7/PortalLookup") },
          { path: "lookup", lazy: () => import("./p7/PortalLookup") },
          { path: "installed-base", lazy: () => import("./p7/InstalledBase") },
          { path: "where-used", lazy: () => import("./p7/WhereUsed") },
          { path: "units/:unitId", lazy: () => import("./p7/PortalUnit") },
        ],
      },
      {
        Component: Frame,
        errorElement: <ErrorPage />,
        children: [
          { index: true, lazy: () => import("./pages/Overview") },
          { path: "search", lazy: () => import("./pages/Search") },
          { path: "lookup", lazy: () => import("./p7/InternalLookup") },
          { path: "imports", lazy: () => import("./p7/ImportJob") },
          { path: "imports/history", lazy: () => import("./p7/ImportHistory") },
          { path: "imports/compare", lazy: () => import("./p7/CompareMode") },
          { path: "reconcile", lazy: () => import("./p7/Reconcile") },
          { path: "reports/identifier-cross-reference", lazy: () => import("./p7/Xref") },
          { path: "integration", lazy: () => import("./integration/ConnectionHealth") },
          { path: "integration/connections", lazy: () => import("./integration/Connections") },
          { path: "integration/messages-attention", lazy: () => import("./integration/MessagesAttention") },
          { path: "integration/manual-tasks", lazy: () => import("./integration/ManualTasks") },
          { path: "integration/daily-check", lazy: () => import("./integration/Reconciliation") },
          { path: "integration/history-loads", lazy: () => import("./integration/HistoryLoads") },
          { path: "settings/disclosure", lazy: () => import("./p7/Disclosure") },
          { path: "units", lazy: () => import("./units/UnitSearch") },
          { path: "units/receive", lazy: () => import("./units/Receive") },
          { path: "units/ranges", lazy: () => import("./units/SerialRanges") },
          { path: "units/completeness", lazy: () => import("./units/Completeness") },
          { path: "units/release", lazy: () => import("./units/Release") },
          { path: "units/:unitId", lazy: () => import("./units/UnitDetail") },
          { path: "entitlements", lazy: () => import("./entitlements/GapReport") },
          { path: "entitlements/cover-changes", lazy: () => import("./entitlements/CoverChanges") },
          { path: "entitlements/gaps", lazy: () => import("./entitlements/GapReport") },
          { path: "entitlements/policies", lazy: () => import("./entitlements/PolicyEditor") },
          { path: "entitlements/bulk", lazy: () => import("./entitlements/BulkOperation") },
          { path: "entitlements/exceptions", lazy: () => import("./entitlements/WarrantyLineExceptions") },
          { path: "approvals", lazy: () => import("./entitlements/ApprovalInbox") },
          { path: "corrections", lazy: () => import("./corrections/Workbench") },
          { path: "corrections/requests/:requestId", lazy: () => import("./corrections/ApproverView") },
          { path: "corrections/kpi", lazy: () => import("./corrections/CorrectionKpi") },
          { path: "corrections/reassign-allocation", lazy: () => import("./corrections/AllocationReassign") },
          { path: "corrections/merge", lazy: () => import("./corrections/MergeUnits") },
          { path: "corrections/vendor-serials", lazy: () => import("./corrections/VendorSerials") },
          { path: "claims", lazy: () => import("./claims/Claims") },
          // Claim collection routes (new, campaigns, recovery) stay above the id route. React Router ranks literals first
          // too; the order documents the boundary so "campaigns" or "recovery" is never treated as a claim id.
          { path: "claims/new", lazy: () => import("./claims/ClaimIntake") },
          { path: "claims/recovery", lazy: () => import("./recovery/RecoveryClaims") },
          // P6 OD-26 numbering settings route block.
          { path: "settings", lazy: () => import("./settings/Settings") },
          { path: "settings/users", lazy: () => import("./settings/Users") },
          // End P6 OD-26 numbering settings route block.
          { path: "settings/time-zone", lazy: () => import("./settings/TimeZoneSettings") },
          { path: "settings/sign-in-policy", lazy: () => import("./settings/SignInPolicy") },
          { path: "settings/import-files", lazy: () => import("./settings/ImportFileSettings") },
          { path: "settings/currencies", lazy: () => import("./settings/Currencies") },
          { path: "settings/recovery", lazy: () => import("./settings/SupplierRecovery") },
          { path: "settings/refurbishment", lazy: () => import("./settings/RefurbishmentPolicy") },
          { path: "settings/workbench-choices", lazy: () => import("./corrections/WorkbenchChoices") },
          { path: "settings/certificates", lazy: () => import("./settings/CertificateSettings") },
          // P6 jurisdiction rules route block.
          { path: "settings/jurisdictions", lazy: () => import("./jurisdictions/Jurisdictions") },
          // End P6 jurisdiction rules route block.
          { path: "replacements/awaiting", lazy: () => import("./replacements/AwaitingReturns") },
          { path: "refurbishments", lazy: () => import("./refurbishments/RefurbishmentList") },
          { path: "refurbishments/:id", lazy: () => import("./refurbishments/Refurbishment") },
          // P6 label screens. Certificate routes remain supplied by the sibling certificate UI change.
          { path: "labels/print-jobs", lazy: () => import("./labels/PrintJobs") },
          { path: "labels/templates/new", lazy: () => import("./labels/LabelTemplateEditor") },
          { path: "labels/templates/:id", lazy: () => import("./labels/LabelTemplateEditor") },
          { path: "settings/labels", lazy: () => import("./settings/LabelSettings") },
          { path: "account/security", lazy: () => import("./pages/AccountSecurity") },
          { path: "claims/campaigns", lazy: () => import("./campaigns/Campaigns") },
          { path: "claims/campaigns/new", lazy: () => import("./campaigns/CampaignEditor") },
          { path: "claims/campaigns/:campaignId", lazy: () => import("./campaigns/CampaignEditor") },
          { path: "claims/campaigns/:campaignId/units", lazy: () => import("./campaigns/CampaignUnits") },
          { path: "claims/:claimId", lazy: () => import("./claims/ClaimAdjudication") },
          // Return collection routes stay above the id route so "receipt" is never treated as an RMA id.
          { path: "returns", lazy: () => import("./returns/Returns") },
          { path: "returns/receipt", lazy: () => import("./returns/ReturnReceipt") },
          { path: "returns/:rmaId", lazy: () => import("./returns/RmaDetail") },
          // P6 issue worktree route block.
          { path: "certificates/issue", lazy: () => import("./certificates/Issue") },
          // End P6 issue worktree route block.
          { path: "replacements/new", lazy: () => import("./returns/WarrantyReplacement") },
          { path: "replacements/:id", lazy: () => import("./returns/WarrantyReplacement") },
          // P6 certificate UI worktree block. Keep sibling label routes outside this block during integration.
          { path: "certificates", lazy: () => import("./certificates/Certificates") },
          { path: "certificates/:id", lazy: () => import("./certificates/CertificateViewer") },
          { path: "templates", lazy: () => import("./certificates/TemplateCatalogue") },
          { path: "templates/certificates/:id", lazy: () => import("./certificates/CertificateDesigner") },
          // End P6 certificate UI worktree block.
          ...patterns,
          ...placeholders,
          { path: "*", lazy: () => import("./pages/Placeholder") },
        ],
      },
    ],
  },
]);
