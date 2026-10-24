import { useEffect, useState } from "react";
import { passwordApi, type PasswordRulesFor } from "./api";
import { DEFAULT_PASSWORD_RULES, ruleChecks, type PasswordRuleSet } from "./passwordRulesModel";

/** The rules that apply to this person; the defaults show until the read returns (or if it fails). */
export function usePasswordRules(whose: PasswordRulesFor): PasswordRuleSet {
  const [rules, setRules] = useState<PasswordRuleSet>(DEFAULT_PASSWORD_RULES);
  const key = JSON.stringify(whose);
  useEffect(() => {
    const controller = new AbortController();
    passwordApi.rules(JSON.parse(key) as PasswordRulesFor, controller.signal).then(setRules).catch(() => undefined);
    return () => controller.abort();
  }, [key]);
  return rules;
}

export function PasswordRules({ password, confirmation, rules, email, displayName }: { password: string; confirmation: string; rules: PasswordRuleSet; email?: string | undefined; displayName?: string | undefined }) {
  const identityKnown = email !== undefined && displayName !== undefined;
  const differsFromIdentity = identityKnown && password.length > 0 && password.toLocaleLowerCase() !== email.toLocaleLowerCase()
    && password.toLocaleLowerCase() !== displayName.toLocaleLowerCase();
  return <ul className="rules" aria-label="Password rules">
    {ruleChecks(password, rules).map((check) => <li key={check.key} className={check.met ? undefined : "todo"}>{check.label}</li>)}
    <li className={differsFromIdentity ? undefined : "todo"}>Not your e-mail or your name{identityKnown ? "" : " (checked when saved)"}</li>
    <li className="todo">Not a common or easy-to-guess password</li>
    <li className={password.length > 0 && password === confirmation ? undefined : "todo"}>Both entries match</li>
  </ul>;
}
