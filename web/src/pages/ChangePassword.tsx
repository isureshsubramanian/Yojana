import { useNavigate, useRevalidator } from "react-router";
import { PasswordChangeForm } from "../security/PasswordChangeForm";
import { useSession } from "../session/session";
import { BareFrame } from "../shell/Frame";

export function Component() {
  const session = useSession();
  const navigate = useNavigate();
  const revalidator = useRevalidator();
  const changed = async () => {
    await revalidator.revalidate();
    navigate(session.tenant ? "/" : "/tenants", { replace: true });
  };
  return <BareFrame><div className="auth"><section className="card" aria-labelledby="h-change-password"><div className="card-body">
    <h1 id="h-change-password">{session.passwordChangeRequired ? "Choose a new password" : "Change password"}</h1>
    <p className="lead">{session.passwordChangeRequired ? "Your temporary password worked. Choose a password only you know before continuing." : "Changing your password signs out every other device."}</p>
    <PasswordChangeForm onChanged={changed} />
  </div></section></div></BareFrame>;
}
