export function PasswordRules({ password, confirmation, email, displayName }: { password: string; confirmation: string; email?: string | undefined; displayName?: string | undefined }) {
  const identityKnown = email !== undefined && displayName !== undefined;
  const differsFromIdentity = identityKnown && password.toLocaleLowerCase() !== email.toLocaleLowerCase()
    && password.toLocaleLowerCase() !== displayName.toLocaleLowerCase();
  return <ul className="rules" aria-label="Password rules">
    <li className={password.length >= 12 ? undefined : "todo"}>At least 12 characters</li>
    <li className={differsFromIdentity ? undefined : "todo"}>Not your e-mail or your name{identityKnown ? "" : " (checked when saved)"}</li>
    <li className={password.length > 0 && password === confirmation ? undefined : "todo"}>Both entries match</li>
  </ul>;
}
