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
  {
    id: ROOT_ROUTE_ID,
    path: "/",
    loader: requireSession,
    errorElement: <ErrorPage />,
    children: [
      { path: "tenants", lazy: () => import("./pages/ChooseTenant") },
      {
        Component: Frame,
        errorElement: <ErrorPage />,
        children: [
          { index: true, lazy: () => import("./pages/Overview") },
          { path: "search", lazy: () => import("./pages/Search") },
          { path: "units", lazy: () => import("./units/UnitSearch") },
          { path: "units/receive", lazy: () => import("./units/Receive") },
          { path: "units/ranges", lazy: () => import("./units/SerialRanges") },
          { path: "units/completeness", lazy: () => import("./units/Completeness") },
          { path: "units/release", lazy: () => import("./units/Release") },
          { path: "units/:unitId", lazy: () => import("./units/UnitDetail") },
          { path: "entitlements", lazy: () => import("./entitlements/GapReport") },
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
          { path: "settings/currencies", lazy: () => import("./settings/Currencies") },
          { path: "settings/recovery", lazy: () => import("./settings/SupplierRecovery") },
          { path: "settings/workbench-choices", lazy: () => import("./corrections/WorkbenchChoices") },
          { path: "claims/campaigns", lazy: () => import("./campaigns/Campaigns") },
          { path: "claims/campaigns/new", lazy: () => import("./campaigns/CampaignEditor") },
          { path: "claims/campaigns/:campaignId", lazy: () => import("./campaigns/CampaignEditor") },
          { path: "claims/campaigns/:campaignId/units", lazy: () => import("./campaigns/CampaignUnits") },
          { path: "claims/:claimId", lazy: () => import("./claims/ClaimAdjudication") },
          // Return collection routes stay above the id route so "receipt" is never treated as an RMA id.
          { path: "returns", lazy: () => import("./returns/Returns") },
          { path: "returns/receipt", lazy: () => import("./returns/ReturnReceipt") },
          { path: "returns/:rmaId", lazy: () => import("./returns/RmaDetail") },
          { path: "replacements/new", lazy: () => import("./returns/WarrantyReplacement") },
          { path: "replacements/:id", lazy: () => import("./returns/WarrantyReplacement") },
          ...patterns,
          ...placeholders,
          { path: "*", lazy: () => import("./pages/Placeholder") },
        ],
      },
    ],
  },
]);
