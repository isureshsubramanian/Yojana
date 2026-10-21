import { Link } from "react-router";
import { claimRiskDecisionLabel, claimRiskRuleLabel } from "./format";
import type { ClaimRiskFlagRow } from "./types";

export function ClaimRiskFlagChip({ flag }: { flag: Pick<ClaimRiskFlagRow, "ruleCode" | "status"> }) {
  return <span className={`claim-risk-chip ${flag.status === "open" ? "open" : "decided"}`}>
    {claimRiskRuleLabel(flag.ruleCode)}{flag.status === "open" ? "" : ` · ${claimRiskDecisionLabel(flag.status).toLowerCase()}`}
  </span>;
}

export function ClaimRiskHoldNotice({ claimId, rules }: { claimId: string; rules: readonly string[] }) {
  const unique = [...new Set(rules)];
  return <div className="note warn claim-risk-hold" role="status">
    <strong>Approval held for risk review.</strong>&nbsp;Each open flag needs a final second-review decision: {unique.map(claimRiskRuleLabel).join(", ")}.
    &nbsp;<Link to={`/claims/risk-review?claim=${encodeURIComponent(claimId)}`}>Review risk flags</Link>
  </div>;
}
