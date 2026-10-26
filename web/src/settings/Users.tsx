import { useCallback, useEffect, useMemo, useState } from "react";
import { Link } from "react-router";
import { ApiError } from "../api/client";
import { ActionDialog, DataGrid, KpiStrip, StatusBadge, useGridQuery, type GridColumn, type PageResult } from "../components";
import { openDrawer, updateDrawer } from "../shell/drawerStore";
import { Breadcrumbs, PageHeader } from "../shell/PageHeader";
import { useSession } from "../session/session";
import { ErrorNote, Kv, Section } from "../units/common";
import { passwordResetApi, type PasswordResetOptions } from "./passwordResetApi";
import { ROLE_GROUPS, roleLabel, STAFF_ROLES } from "./roles";
import { tenantUserPage, userManagementApi, type TenantUserDetail, type TenantUserRow, type TenantUserSummary } from "./userManagementApi";

const PAGE_SIZE = 50;
const when = (value: string | null) => value ? new Date(value).toLocaleString() : "Never";
const statusLabel = (row: TenantUserRow) => row.status === "locked" ? `Locked until ${when(row.lockedUntil)}` :
  row.status === "invited" ? `Invited · expires ${when(row.invitationExpiresAt)}` :
    row.status === "deactivated" ? `Deactivated ${when(row.deactivatedAt)}` : "Active";
const statusTone = (status: TenantUserRow["status"]) => status === "active" ? "ok" : status === "deactivated" ? "bad" : "warn";

type Dialog = { kind: "invite" } | { kind: "deactivate"; row: TenantUserRow } |
  { kind: "temporary"; password: string } | { kind: "second-step-reset"; row: TenantUserRow } | null;

export function Component() {
  const session = useSession();
  const permitted = session.roles.includes("tenant_administrator");
  const [summary, setSummary] = useState<TenantUserSummary | null>(null);
  const [status, setStatus] = useState("all");
  const [role, setRole] = useState("");
  const [q, setQ] = useState("");
  const [error, setError] = useState<unknown>(null);
  const [dialog, setDialog] = useState<Dialog>(null);
  const filters = useMemo(() => { const value = new URLSearchParams({ status }); if (role) value.set("role", role); if (q.trim()) value.set("q", q.trim()); return value; }, [q, role, status]);
  const query = useGridQuery("/api/security/users", tenantUserPage, {
    gridId: "security-users", userId: session.user.id, defaultSort: { key: "default", dir: "asc" },
    defaultSize: PAGE_SIZE, params: filters, paging: "cursor", sortKeys: ["default"], enabled: permitted,
    pageFacts: value => ({ continuation: value.nextCursor }),
  });
  const rows = query.data?.items ?? [];
  useEffect(() => { if (!permitted) return; const controller = new AbortController(); setError(null);
    void userManagementApi.summary(controller.signal).then(setSummary)
      .catch(reason => { if (!controller.signal.aborted) setError(reason); });
    return () => controller.abort();
  }, [permitted]);
  const changed = query.reload;
  const show = useCallback((row: TenantUserRow) => {
    openDrawer({ eyebrow: row.invitationId ? "Pending invitation" : "Tenant user", title: row.displayName },
      <UserDrawer row={row} onChanged={changed} onDeactivate={() => setDialog({ kind: "deactivate", row })}
        onTemporary={(password) => setDialog({ kind: "temporary", password })}
        onSecondStepReset={() => setDialog({ kind: "second-step-reset", row })} />);
  }, [changed]);

  const columns = useMemo<GridColumn<TenantUserRow>[]>(() => [
    { key: "person", label: "User", width: 17, render: (row) => <span className="primary">{row.displayName}</span> },
    { key: "email", label: "E-mail", width: 20, render: (row) => row.email },
    { key: "roles", label: "Roles", width: 23, render: (row) => row.roles.map(roleLabel).join(", ") },
    { key: "methods", label: "Sign-in methods", width: 13, render: (row) => row.signInMethods.length ? row.signInMethods.map((method) => method === "app" ? "App" : method === "passkey" ? passkeyCount(row.passkeys) : roleLabel(method)).join(", ") : "Not set up" },
    { key: "last", label: "Last sign-in", width: 14, render: (row) => when(row.lastSignInAt), muted: true },
    { key: "status", label: "Status", width: 17, render: (row) => <StatusBadge tone={statusTone(row.status)}>{statusLabel(row)}</StatusBadge> },
  ], []);
  const estimatedTotal = (query.page - 1) * query.pageSize + rows.length + (query.data?.nextCursor ? 1 : 0);
  const grid: PageResult<TenantUserRow> = { rows, page: query.page, pageSize: query.pageSize, total: estimatedTotal,
    continuation: query.data?.nextCursor ?? null, totalIsExact: false, hasMore: !!query.data?.nextCursor };
  return <main className="content" id="main" tabIndex={-1}>
    <Breadcrumbs crumbs={[{ label: "Home", to: "/" }, { label: "Settings", to: "/settings" }, { label: "Users" }]} />
    <PageHeader eyebrow="Settings · tenant administrator" title="Users"
      sub="Staff memberships for this company. Customer-portal memberships are managed separately and are not listed here."
      actions={permitted && <button type="button" className="btn primary" onClick={() => setDialog({ kind: "invite" })}>Invite user…</button>} />
    <nav className="tabs subnav" aria-label="Sign-in settings"><Link to="/settings/users" aria-current="page">Users</Link><Link to="/settings/sign-in-policy">Sign-in policy</Link><Link to="/settings/security-events">Security events</Link></nav>
    {!permitted && <div className="note bad"><strong>Tenant administrator access is required.</strong></div>}
    {permitted && <div className="stack" style={{ marginTop: 20 }}>
      <ErrorNote error={error ?? query.error} onReload={query.reload} />
      <KpiStrip label="User figures" kpis={[
        { label: "Active", value: summary?.active.toLocaleString() ?? "—" },
        { label: "Invited", value: summary?.invited.toLocaleString() ?? "—" },
        { label: "Deactivated", value: summary?.deactivated.toLocaleString() ?? "—" },
        { label: "Second step on", value: summary ? `${summary.active ? Math.round(summary.secondStepOn * 100 / summary.active) : 0}%` : "—", detail: summary ? `${summary.secondStepOn} of ${summary.active}` : null },
        { label: "Required but not set up", value: summary?.requiredNotSetUp.toLocaleString() ?? "—", detail: "asked at next sign-in" },
      ]} />
      <section className="card" aria-label="Tenant users">
        <div className="card-header" style={{ gap: 12, flexWrap: "wrap" }}>
          <div className="segmented plain" role="group" aria-label="User status">
            {([["all", "All"], ["invited", "Invited"], ["no_second_step", "No second step"], ["deactivated", "Deactivated"]] as const).map(([value, label]) =>
              <button type="button" key={value} aria-pressed={status === value} onClick={() => setStatus(value)}>{label}</button>)}
          </div>
          <input className="input" aria-label="Search users" placeholder="Search name or e-mail" value={q} onChange={(event) => setQ(event.target.value)} />
          <select className="select" aria-label="Filter by role" value={role} onChange={(event) => setRole(event.target.value)}>
            <option value="">All roles</option>{STAFF_ROLES.map((value) => <option value={value} key={value}>{roleLabel(value)}</option>)}
          </select>
        </div>
        <DataGrid columns={columns} page={grid} rowId={(row) => row.userId ?? row.invitationId!} onPage={query.onPage}
          onRowClick={show} loading={query.loading} viewportHeight={560} emptyText="No staff users match these filters."
          gridId="security-users" userId={session.user.id} sort={query.sort} defaultSort={{ key: "default", dir: "asc" }} onSort={query.onSort}
          pageSize={query.pageSize} defaultPageSize={PAGE_SIZE} onPageSize={query.onPageSize} />
      </section>
    </div>}
    {dialog?.kind === "invite" && <InviteDialog onCancel={() => setDialog(null)} onSaved={() => { setDialog(null); changed(); }} />}
    {dialog?.kind === "deactivate" && <DeactivateDialog row={dialog.row} onCancel={() => setDialog(null)} onSaved={() => { setDialog(null); changed(); }} />}
    {dialog?.kind === "temporary" && <TemporaryPasswordDialog password={dialog.password} onClose={() => setDialog(null)} />}
    {dialog?.kind === "second-step-reset" && <SecondStepResetDialog row={dialog.row} onCancel={() => setDialog(null)} onSaved={() => { setDialog(null); changed(); }} />}
  </main>;
}

function UserDrawer({ row, onChanged, onDeactivate, onTemporary, onSecondStepReset }: { row: TenantUserRow; onChanged: () => void; onDeactivate: () => void; onTemporary: (password: string) => void; onSecondStepReset: () => void }) {
  const [detail, setDetail] = useState<TenantUserDetail | null>(null);
  const [roles, setRoles] = useState(row.roles);
  const [reset, setReset] = useState<PasswordResetOptions | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  useEffect(() => {
    if (!row.userId) return;
    const controller = new AbortController();
    void Promise.all([userManagementApi.detail(row.userId, controller.signal), passwordResetApi.options(row.userId, controller.signal)])
      .then(([user, options]) => { setDetail(user); setRoles(user.user.roles); setReset(options); })
      .catch((reason) => { if (!(reason instanceof DOMException && reason.name === "AbortError")) setError(reason); });
    return () => controller.abort();
  }, [row.userId]);
  const run = async (work: () => Promise<unknown>) => { setBusy(true); setError(null); try { await work(); onChanged(); } catch (reason) { setError(reason); } finally { setBusy(false); } };
  if (row.invitationId) return <><Section title="Invitation"><Kv rows={[["E-mail", row.email], ["Roles", row.roles.map(roleLabel).join(", ")], ["Expires", when(row.invitationExpiresAt)]]} /></Section><Section title="Actions"><div className="act"><button type="button" className="btn primary block" disabled={busy} onClick={() => void run(() => userManagementApi.resend(row.invitationId!))}>Resend invitation</button><button type="button" className="btn block" disabled={busy} onClick={() => void run(() => userManagementApi.revoke(row.invitationId!))}>Revoke invitation</button></div><ErrorNote error={error} /></Section></>;
  const current = detail?.user ?? row;
  const other = [...(detail?.alsoMemberOf ?? []), ...(detail?.otherMembershipCount ? [`and ${detail.otherMembershipCount} other ${detail.otherMembershipCount === 1 ? "company" : "companies"}`] : [])];
  const unavailable = reset?.temporaryUnavailableReason === "tenant_setting_off" ? "Temporary passwords are off in the tenant policy." : reset?.temporaryUnavailableReason === "other_memberships" ? "This user has memberships you do not administer." : reset?.temporaryUnavailableReason === "no_password_sign_in" ? "This account does not sign in with a password." : "Temporary password is unavailable.";
  const saveRoles = () => current.userId && current.version && run(async () => {
    const updated = await userManagementApi.roles(current.userId!, roles, current.version!);
    setDetail((value) => value ? { ...value, user: updated } : value);
    updateDrawer({ eyebrow: "Tenant user", title: updated.displayName }, <UserDrawer row={updated} onChanged={onChanged} onDeactivate={onDeactivate} onTemporary={onTemporary} onSecondStepReset={onSecondStepReset} />);
  });
  const passwordReset = (mode: "link" | "temporary") => current.userId && run(async () => {
    const result = await passwordResetApi.reset(current.userId!, mode);
    if (result.temporaryPassword) onTemporary(result.temporaryPassword);
  });
  return <>
    {current.status === "locked" && <div className="note warn"><strong>Sign-in is temporarily locked.</strong>&nbsp;Locked until {when(current.lockedUntil)}.</div>}
    <Section title="Details"><Kv rows={[["E-mail", current.email], ["Second step", secondStepLine(current)], ["Also a member of", other.length ? other.join(", ") : "No other company visible to you"], ["Password changed", when(detail?.passwordChangedAt ?? null)]]} /><p className="muted">You can name only companies where you are also a tenant administrator.</p></Section>
    <Section title="Roles"><RoleChecks roles={roles} setRoles={setRoles} /><button type="button" className="btn primary block" disabled={busy || roles.length === 0 || !detail || roles.slice().sort().join() === current.roles.slice().sort().join()} onClick={() => void saveRoles()}>Save roles</button></Section>
    <Section title="Password"><div className="act"><button type="button" className="btn primary block" disabled={busy || reset?.canSendLink === false} onClick={() => void passwordReset("link")}>Send password reset link</button><button type="button" className="btn block" disabled={busy || !reset?.canSetTemporary} title={!reset?.canSetTemporary ? unavailable : undefined} onClick={() => void passwordReset("temporary")}>Set a temporary password</button></div><p className="hint">You can reset a user only when you administer every company they belong to. For anyone else, the reset link goes to their own e-mail and nothing else is offered.</p></Section>
    <Section title="Second step"><button type="button" className="btn block" disabled={busy || !(current.signInMethods.includes("app") || current.signInMethods.includes("passkey")) || !detail?.secondStepResetAllowed} title={!detail?.secondStepResetAllowed ? "You can reset the second step only when you administer every company this user belongs to; use your account page for yourself." : undefined} onClick={onSecondStepReset}>Reset second step…</button></Section>
    <Section title="Membership"><div className="act">{current.status === "deactivated" ? <button type="button" className="btn block" disabled={busy} onClick={() => current.userId && void run(() => userManagementApi.reactivate(current.userId!))}>Reactivate</button> : <button type="button" className="btn block" disabled={busy} onClick={onDeactivate}>Deactivate…</button>}</div></Section>
    <ErrorNote error={error} />
  </>;
}

function RoleChecks({ roles, setRoles }: { roles: string[]; setRoles: (roles: string[]) => void }) {
  const toggle = (role: string, checked: boolean) => setRoles(checked ? [...roles, role] : roles.filter((value) => value !== role));
  return <div className="role-groups">{ROLE_GROUPS.map((group) => <fieldset className="role-group" key={group.label}>
    <legend>{group.label}</legend>
    {group.roles.map((role) => <label className="checkrow" key={role}><input type="checkbox" checked={roles.includes(role)} onChange={(event) => toggle(role, event.target.checked)} />{roleLabel(role)}</label>)}
  </fieldset>)}</div>;
}

function InviteDialog({ onCancel, onSaved }: { onCancel: () => void; onSaved: () => void }) {
  const [email, setEmail] = useState(""); const [name, setName] = useState(""); const [roles, setRoles] = useState<string[]>([]);
  const [busy, setBusy] = useState(false); const [error, setError] = useState<unknown>(null);
  const save = async () => { setBusy(true); setError(null); try { await userManagementApi.invite(email, name, roles); onSaved(); } catch (reason) { setError(reason); } finally { setBusy(false); } };
  const inline = error instanceof ApiError && error.title === "already_member" ? "That address is already an active member of this company." : error instanceof ApiError && error.title === "already_invited" ? "An open invitation already exists. Select it in the list to resend it." : null;
  return <ActionDialog title="Invite user" onCancel={onCancel} wide actions={<button type="button" className="btn primary" disabled={busy || !email.trim() || !name.trim() || roles.length === 0} onClick={() => void save()}>{busy ? "Sending…" : "Send invitation"}</button>}>
    <div className="form-grid"><div className="field"><label htmlFor="invite-email">E-mail</label><input id="invite-email" className="input" type="email" value={email} onChange={(event) => setEmail(event.target.value)} /></div><div className="field"><label htmlFor="invite-name">Name</label><input id="invite-name" className="input" value={name} onChange={(event) => setName(event.target.value)} /></div><div className="field span"><span className="lbl">Roles <span className="meta">· {roles.length ? `${roles.length} selected` : "choose at least one"}</span></span><RoleChecks roles={roles} setRoles={setRoles} /></div></div>
    {inline ? <div className="form-error" role="alert">{inline}</div> : <ErrorNote error={error} />}
  </ActionDialog>;
}

function DeactivateDialog({ row, onCancel, onSaved }: { row: TenantUserRow; onCancel: () => void; onSaved: () => void }) {
  const [reason, setReason] = useState(""); const [busy, setBusy] = useState(false); const [error, setError] = useState<unknown>(null);
  const save = async () => { if (!row.userId) return; setBusy(true); try { await userManagementApi.deactivate(row.userId, reason); onSaved(); } catch (value) { setError(value); } finally { setBusy(false); } };
  return <ActionDialog title={`Deactivate ${row.displayName}`} onCancel={onCancel} actions={<button type="button" className="btn primary" disabled={busy || !reason.trim()} onClick={() => void save()}>Deactivate membership</button>}><p className="muted">This removes access to this company only. Other company memberships remain active.</p><div className="field"><label htmlFor="deactivate-reason">Reason</label><textarea id="deactivate-reason" className="textarea" value={reason} onChange={(event) => setReason(event.target.value)} /></div><ErrorNote error={error} /></ActionDialog>;
}

function TemporaryPasswordDialog({ password, onClose }: { password: string; onClose: () => void }) {
  const [copied, setCopied] = useState(false);
  return <ActionDialog title="Temporary password" onCancel={onClose} actions={<button type="button" className="btn primary" onClick={onClose}>Done</button>}><div className="note warn"><strong>Shown once.</strong>&nbsp;Copy it now and share it securely. It is not shown again.</div><div className="field"><span className="lbl">Temporary password</span><div className="cluster"><code>{password}</code><button type="button" className="btn" onClick={() => void navigator.clipboard.writeText(password).then(() => setCopied(true))}>{copied ? "Copied" : "Copy"}</button></div></div></ActionDialog>;
}

function SecondStepResetDialog({ row, onCancel, onSaved }: { row: TenantUserRow; onCancel: () => void; onSaved: () => void }) {
  const [reason, setReason] = useState(""); const [busy, setBusy] = useState(false); const [error, setError] = useState<unknown>(null);
  const save = async () => { if (!row.userId) return; setBusy(true); setError(null); try { await userManagementApi.resetSecondStep(row.userId, reason); onSaved(); } catch (value) { setError(value); } finally { setBusy(false); } };
  return <ActionDialog title={`Reset second step for ${row.displayName}`} onCancel={onCancel} actions={<button type="button" className="btn primary" disabled={busy || !reason.trim()} onClick={() => void save()}>Reset second step</button>}><p className="muted">This removes the authenticator and all recovery codes, ends the user’s sessions and tells them by e-mail.</p><label className="field"><span className="lbl">Reason</span><textarea className="textarea" value={reason} onChange={(event) => setReason(event.target.value)} /></label><ErrorNote error={error} /></ActionDialog>;
}

const passkeyCount = (count: number) => count === 1 ? "1 passkey" : `${count} passkeys`;

/** The authenticator app and passkeys both count; a reset removes both. */
function secondStepLine(row: TenantUserRow) {
  const parts = [row.signInMethods.includes("app") ? `Authenticator app · ${row.recoveryCodesLeft} recovery codes left` : null, row.passkeys > 0 ? passkeyCount(row.passkeys) : null].filter(Boolean);
  return parts.length ? parts.join(" · ") : "Not set up";
}
