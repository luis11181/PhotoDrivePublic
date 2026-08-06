"use client";

import { useQuery, useQueryClient } from "@tanstack/react-query";
import {
  Copy,
  KeyRound,
  Loader2,
  RefreshCw,
  ShieldCheck,
  UserPlus,
} from "lucide-react";
import { FormEvent, useState } from "react";
import { BackToLibraryButton } from "@/components/back-to-library-button";
import {
  inviteManagedUser,
  issuePasswordReset,
  listManagedUsers,
  revokeUserSessions,
  updateManagedUser,
  type CurrentUser,
  type IssuedResetCode,
  type ManagedUser,
  type SignInOptions,
} from "@/lib/auth";
import { usePreferences } from "@/lib/preferences";

export function UserManagement({
  currentUser,
  signInOptions,
}: {
  currentUser: CurrentUser;
  signInOptions: SignInOptions;
}) {
  const { t } = usePreferences();
  const queryClient = useQueryClient();
  const users = useQuery({
    queryKey: ["managed-users"],
    queryFn: listManagedUsers,
    enabled: currentUser.isOwner,
  });
  const [issued, setIssued] = useState<IssuedResetCode | null>(null);
  const [message, setMessage] = useState("");
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(false);

  if (!currentUser.isOwner)
    return (
      <main className="users-page">
        <section className="users-panel users-denied">
          <ShieldCheck />
          <h1>{t("users.ownerRequired")}</h1>
          <p>{t("users.ownerRequiredHelp")}</p>
          <BackToLibraryButton className="auth-return-link" label={t("users.returnFiles")} fallbackHref="/files" />
        </section>
      </main>
    );

  async function run(action: () => Promise<void>) {
    setBusy(true);
    setError("");
    setMessage("");
    try {
      await action();
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : t("users.actionFailed"));
    } finally {
      setBusy(false);
    }
  }

  return (
    <main className="users-page">
      <header className="users-header">
        <div>
          <p className="auth-eyebrow">{t("users.eyebrow")}</p>
          <h1>{t("users.title")}</h1>
          <p>{t("users.intro")}</p>
        </div>
        <BackToLibraryButton className="auth-secondary users-back" label={t("users.backLibrary")} fallbackHref="/files" />
      </header>

      {(error || message) && (
        <p className={error ? "auth-error" : "auth-success"} role="status">
          {error || message}
        </p>
      )}
      {issued && (
        <SecretPanel
          title={t("users.oneTimeCode")}
          copy={t("users.oneTimeCodeHelp")}
          values={[issued.code]}
          onClose={() => setIssued(null)}
        />
      )}
      <InviteForm
        options={signInOptions}
        busy={busy}
        onSubmit={(request) =>
          run(async () => {
            const result = await inviteManagedUser(request);
            setIssued(result.passwordSetup ?? null);
            setMessage(t("users.allowlisted", { email: result.user.email }));
            await queryClient.invalidateQueries({ queryKey: ["managed-users"] });
          })
        }
      />

      <section className="users-panel">
        <div className="users-section-heading">
          <div>
            <h2>{t("users.allowedPeople")}</h2>
            <p>{t("users.allowedPeopleHelp")}</p>
          </div>
        </div>
        {users.isPending ? (
          <Loader2 className="auth-spinner" />
        ) : users.error ? (
          <p className="auth-error">{t("users.loadFailed")}</p>
        ) : (
          <div className="managed-user-list">
            {users.data?.map((user) => (
              <ManagedUserCard
                key={user.id}
                user={user}
                options={signInOptions}
                busy={busy}
                onSave={(request) =>
                  run(async () => {
                    await updateManagedUser(user.id, request);
                    setMessage(t("users.updated", { email: user.email }));
                    await queryClient.invalidateQueries({
                      queryKey: ["managed-users"],
                    });
                  })
                }
                onReset={() =>
                  run(async () => setIssued(await issuePasswordReset(user.id)))
                }
                onRevoke={() =>
                  run(async () => {
                    await revokeUserSessions(user.id);
                    setMessage(t("users.signedOut", { email: user.email }));
                    await queryClient.invalidateQueries({
                      queryKey: ["managed-users"],
                    });
                  })
                }
              />
            ))}
          </div>
        )}
      </section>
    </main>
  );
}

function InviteForm({
  options,
  busy,
  onSubmit,
}: {
  options: SignInOptions;
  busy: boolean;
  onSubmit: (request: {
    email: string;
    displayName: string;
    role: "OWNER" | "MEMBER";
    googleAllowed: boolean;
    passwordAllowed: boolean;
  }) => void;
}) {
  const { t } = usePreferences();
  const [email, setEmail] = useState("");
  const [displayName, setDisplayName] = useState("");
  const [role, setRole] = useState<"OWNER" | "MEMBER">("MEMBER");
  const [googleAllowed, setGoogleAllowed] = useState(options.googleEnabled);
  const [passwordAllowed, setPasswordAllowed] = useState(
    !options.googleEnabled && options.passwordEnabled,
  );
  function submit(event: FormEvent) {
    event.preventDefault();
    onSubmit({ email, displayName, role, googleAllowed, passwordAllowed });
    setEmail("");
    setDisplayName("");
  }
  return (
    <section className="users-panel">
      <div className="users-section-heading">
        <div>
          <h2>{t("users.invite")}</h2>
          <p>{t("users.inviteHelp")}</p>
        </div>
        <UserPlus />
      </div>
      <form className="users-invite-form" onSubmit={submit}>
        <label>
          {t("users.name")}
          <input required value={displayName} onChange={(event) => setDisplayName(event.target.value)} />
        </label>
        <label>
          {t("users.email")}
          <input required type="email" value={email} onChange={(event) => setEmail(event.target.value)} />
        </label>
        <label>
          {t("users.role")}
          <select value={role} onChange={(event) => setRole(event.target.value as "OWNER" | "MEMBER")}>
            <option value="MEMBER">{t("users.member")}</option>
            <option value="OWNER">{t("users.owner")}</option>
          </select>
        </label>
        <MethodChecks
          options={options}
          google={googleAllowed}
          password={passwordAllowed}
          onGoogle={setGoogleAllowed}
          onPassword={setPasswordAllowed}
        />
        <button className="auth-primary" disabled={busy || (!googleAllowed && !passwordAllowed)} type="submit">
          {busy ? <Loader2 className="spin" /> : <UserPlus />}{" "}
          {t("users.allowAccount")}
        </button>
      </form>
    </section>
  );
}

function ManagedUserCard({
  user,
  options,
  busy,
  onSave,
  onReset,
  onRevoke,
}: {
  user: ManagedUser;
  options: SignInOptions;
  busy: boolean;
  onSave: (request: {
    displayName: string;
    role: "OWNER" | "MEMBER";
    status: "Invited" | "Active" | "Disabled";
    googleAllowed: boolean;
    passwordAllowed: boolean;
  }) => void;
  onReset: () => void;
  onRevoke: () => void;
}) {
  const { t } = usePreferences();
  const [role, setRole] = useState(user.role);
  const [status, setStatus] = useState(user.status);
  const [google, setGoogle] = useState(user.googleAllowed);
  const [password, setPassword] = useState(user.passwordAllowed);
  return (
    <article className="managed-user-card">
      <div className="managed-user-title">
        <div>
          <strong>{user.displayName}</strong>
          <span>{user.email}</span>
        </div>
        <small>
          {t("users.activeSessions", { count: user.activeSessionCount })}
        </small>
      </div>
      <div className="managed-user-controls">
        <label>
          {t("users.role")}
          <select value={role} onChange={(event) => setRole(event.target.value as "OWNER" | "MEMBER")}>
            <option value="MEMBER">{t("users.member")}</option>
            <option value="OWNER">{t("users.owner")}</option>
          </select>
        </label>
        <label>
          {t("users.status")}
          <select value={status} onChange={(event) => setStatus(event.target.value as ManagedUser["status"])}>
            <option value="Invited">{t("users.invited")}</option>
            <option value="Active">{t("users.active")}</option>
            <option value="Disabled">{t("users.disabled")}</option>
          </select>
        </label>
        <MethodChecks
          options={options}
          google={google}
          password={password}
          onGoogle={setGoogle}
          onPassword={setPassword}
        />
      </div>
      <p className="managed-user-facts">
        {user.googleLinked
          ? t("users.googleLinked")
          : t("users.googleNotLinked")}{" "}
        ·{" "}
        {user.hasPassword
          ? t("users.passwordSet")
          : t("users.passwordNotSet")}
      </p>
      <div className="managed-user-actions">
        <button disabled={busy || (!google && !password)} onClick={() => onSave({
          displayName: user.displayName,
          role,
          status,
          googleAllowed: google,
          passwordAllowed: password,
        })} type="button">{t("users.saveSignOut")}</button>
        {password && <button disabled={busy} onClick={onReset} type="button"><KeyRound /> {t("users.resetPassword")}</button>}
        <button disabled={busy} onClick={onRevoke} type="button"><RefreshCw /> {t("users.revokeSessions")}</button>
      </div>
    </article>
  );
}

function MethodChecks({
  options,
  google,
  password,
  onGoogle,
  onPassword,
}: {
  options: SignInOptions;
  google: boolean;
  password: boolean;
  onGoogle: (value: boolean) => void;
  onPassword: (value: boolean) => void;
}) {
  const { t } = usePreferences();
  return (
    <fieldset className="method-checks">
      <legend>{t("users.allowedSignIn")}</legend>
      {options.googleEnabled && <label><input checked={google} onChange={(event) => onGoogle(event.target.checked)} type="checkbox" /> Google</label>}
      {options.passwordEnabled && <label><input checked={password} onChange={(event) => onPassword(event.target.checked)} type="checkbox" /> {t("users.password")}</label>}
    </fieldset>
  );
}

function SecretPanel({
  title,
  copy,
  values,
  onClose,
}: {
  title: string;
  copy: string;
  values: string[];
  onClose: () => void;
}) {
  const { t } = usePreferences();
  async function copyValues() {
    await navigator.clipboard.writeText(values.join("\n"));
  }
  return (
    <section className="users-panel secret-panel" role="status">
      <div className="users-section-heading"><div><h2>{title}</h2><p>{copy}</p></div></div>
      <pre>{values.join("\n")}</pre>
      <div className="managed-user-actions">
        <button onClick={copyValues} type="button"><Copy /> {t("users.copy")}</button>
        <button onClick={onClose} type="button">{t("users.stored")}</button>
      </div>
    </section>
  );
}
