// Whether the sign-in page leads with "Sign in with a passkey" on this browser. The company is not known until after
// sign-in, and asking the server for it from an email would tell a stranger which emails have accounts, so the browser
// remembers instead: after each sign-in here (and on the account page) it records whether their company allows a passkey
// alone and they hold one, and a passkey_not_allowed answer clears it. It stores no email or account detail.
const KEY = "yj-passkey-first";

export function passkeyFirst(): boolean {
  try {
    return localStorage.getItem(KEY) === "1";
  } catch {
    return false;
  }
}

export function rememberPasskeyFirst(on: boolean): void {
  try {
    if (on) localStorage.setItem(KEY, "1");
    else localStorage.removeItem(KEY);
  } catch {
    /* private mode or storage disabled: the sign-in page simply leads with the password form */
  }
}
