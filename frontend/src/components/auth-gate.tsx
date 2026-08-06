"use client";

import { useQuery, useQueryClient } from "@tanstack/react-query";
import {
  ArrowLeft,
  Check,
  Eye,
  EyeOff,
  KeyRound,
  Loader2,
  LogIn,
  LogOut,
  RefreshCw,
  ShieldCheck,
} from "lucide-react";
import Link from "next/link";
import { FormEvent, useCallback, useEffect, useRef, useState, useSyncExternalStore } from "react";
import { PreferenceControls } from "@/components/preference-controls";
import {
  apiEnabled,
  authSessionExpiredEvent,
  authRequest,
  getCurrentUser,
  getSignInOptions,
  resetPassword,
  resetAuthenticationState,
  type AuthPayload,
  type CurrentUser,
  type SignInOptions,
} from "@/lib/auth";
import { LibraryApp } from "@/features/library/library-app";
import { UserManagement } from "@/features/users/user-management";
import { ImportManagement } from "@/features/imports/import-management";
import { DeveloperOperations } from "@/features/developer/developer-operations";
import { AlbumsApp } from "@/features/albums/albums-app";
import { UploadQueueOverlay } from "@/features/uploads/upload-queue-overlay";
import { PageNavigationShell } from "@/components/page-navigation-shell";
import { useUploadQueue } from "@/features/uploads/upload-queue-provider";
import { usePreferences } from "@/lib/preferences";
import type { LibraryView } from "@/lib/types";

type GoogleCredentialResponse = { credential?: string };
type GoogleIdentityServices = {
  accounts: {
    id: {
      initialize: (config: {
        client_id: string;
        callback: (response: GoogleCredentialResponse) => void;
      }) => void;
      renderButton: (
        parent: HTMLElement,
        options: {
          theme: "outline";
          size: "large";
          type: "standard";
          shape: "rectangular";
          text: "continue_with";
          width: string;
        },
      ) => void;
    };
  };
};
declare global {
  interface Window {
    google?: GoogleIdentityServices;
  }
}

const googleScriptId = "google-identity-services";

function isGoogleSignInOriginSupported() {
  if (typeof window === "undefined") return false;

  const { hostname, protocol } = window.location;
  return (
    protocol === "https:" ||
    hostname === "localhost" ||
    hostname === "127.0.0.1" ||
    hostname === "::1" ||
    hostname === "[::1]"
  );
}

function subscribeToGoogleSignInOrigin() {
  return () => {};
}

export function AuthGate({ view }: { view: LibraryView | "users" | "imports" | "developer" }) {
  const queryClient = useQueryClient();
  const { t } = usePreferences();
  const { activateUser, deactivateUser } = useUploadQueue();
  const [accountOpen, setAccountOpen] = useState(false);
  const session = useQuery({
    queryKey: ["session"],
    queryFn: getCurrentUser,
    enabled: apiEnabled,
    retry: false,
    staleTime: 5 * 60_000,
  });
  const signInOptions = useQuery({
    queryKey: ["sign-in-options"],
    queryFn: getSignInOptions,
    enabled: apiEnabled,
    staleTime: 5 * 60_000,
  });
  const setUser = (user: CurrentUser | null) =>
    queryClient.setQueryData(["session"], user);
  useEffect(() => {
    if (session.data) void activateUser(session.data.id);
    else if (!session.isPending) deactivateUser();
  }, [activateUser, deactivateUser, session.data, session.isPending]);
  useEffect(() => {
    const handleExpiredSession = () => {
      setAccountOpen(false);
      deactivateUser();
      queryClient.setQueryData(["session"], null);
    };
    window.addEventListener(authSessionExpiredEvent, handleExpiredSession);
    return () =>
      window.removeEventListener(authSessionExpiredEvent, handleExpiredSession);
  }, [deactivateUser, queryClient]);
  const signOut = async () => {
    try {
      await authRequest("/api/v1/users/logout", { method: "POST" });
    } finally {
      resetAuthenticationState();
      setAccountOpen(false);
      deactivateUser();
      setUser(null);
    }
  };

  if (!apiEnabled)
    return view === "users" || view === "imports" || view === "developer" || view === "albums" ? (
      <main className="auth-page">
        <p className="auth-note">{t("auth.backendRequired")}</p>
      </main>
    ) : (
      <LibraryApp view={view} demoMode />
    );
  if (session.isPending || signInOptions.isPending)
    return (
      <main className="auth-page">
        <Loader2 className="auth-spinner" aria-label={t("auth.loading")} />
      </main>
    );
  if (!session.data)
    return (
      <LoginView
        onAuthenticated={setUser}
        optionsUnavailable={signInOptions.isError}
        optionsRefreshing={signInOptions.isFetching}
        onRetryOptions={() => void signInOptions.refetch()}
        options={
          signInOptions.data ?? {
            googleEnabled: false,
            passwordEnabled: false,
          }
        }
      />
    );
  if (view === "users")
    return (
      <>
        <PageNavigationShell view="users" user={session.data} onOpenAccount={() => setAccountOpen(true)} onSignOut={signOut}>
          <UserManagement
            currentUser={session.data}
            signInOptions={
              signInOptions.data ?? {
                googleEnabled: false,
                passwordEnabled: false,
              }
            }
          />
        </PageNavigationShell>
        <UploadQueueOverlay />
        {(accountOpen || session.data.mustChangePassword) && (
          <AccountDialog user={session.data} forced={session.data.mustChangePassword} onClose={() => setAccountOpen(false)} onLogout={() => setUser(null)} onPasswordChanged={() => setUser(null)} />
        )}
      </>
    );
  if (view === "imports")
    return (
      <>
        <PageNavigationShell view="imports" user={session.data} onOpenAccount={() => setAccountOpen(true)} onSignOut={signOut}>
          <ImportManagement />
        </PageNavigationShell>
        <UploadQueueOverlay />
        {(accountOpen || session.data.mustChangePassword) && (
          <AccountDialog user={session.data} forced={session.data.mustChangePassword} onClose={() => setAccountOpen(false)} onLogout={() => setUser(null)} onPasswordChanged={() => setUser(null)} />
        )}
      </>
    );
  if (view === "developer")
    return (
      <>
        <PageNavigationShell view="developer" user={session.data} onOpenAccount={() => setAccountOpen(true)} onSignOut={signOut}>
          <DeveloperOperations currentUser={session.data} />
        </PageNavigationShell>
        <UploadQueueOverlay />
        {(accountOpen || session.data.mustChangePassword) && (
          <AccountDialog user={session.data} forced={session.data.mustChangePassword} onClose={() => setAccountOpen(false)} onLogout={() => setUser(null)} onPasswordChanged={() => setUser(null)} />
        )}
      </>
    );
  if (view === "albums")
    return <>
      <AlbumsApp user={session.data} onOpenAccount={() => setAccountOpen(true)} onSignOut={signOut} />
      <UploadQueueOverlay />
      {(accountOpen || session.data.mustChangePassword) && (
        <AccountDialog user={session.data} forced={session.data.mustChangePassword} onClose={() => setAccountOpen(false)} onLogout={() => setUser(null)} onPasswordChanged={() => setUser(null)} />
      )}
    </>;
  return (
    <>
      <LibraryApp
        view={view}
        user={session.data}
        onOpenAccount={() => setAccountOpen(true)}
        onSignOut={signOut}
      />
      <UploadQueueOverlay />
      {(accountOpen || session.data.mustChangePassword) && (
        <AccountDialog
          user={session.data}
          forced={session.data.mustChangePassword}
          onClose={() => setAccountOpen(false)}
          onLogout={() => setUser(null)}
          onPasswordChanged={() => setUser(null)}
        />
      )}
    </>
  );
}

function LoginView({
  onAuthenticated,
  onRetryOptions,
  options,
  optionsRefreshing,
  optionsUnavailable,
}: {
  onAuthenticated: (user: CurrentUser) => void;
  onRetryOptions: () => void;
  options: SignInOptions;
  optionsRefreshing: boolean;
  optionsUnavailable: boolean;
}) {
  const { locale, t } = usePreferences();
  const googleButtonRef = useRef<HTMLDivElement>(null);
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [googleRendered, setGoogleRendered] = useState(false);
  const googleOriginSupported = useSyncExternalStore(
    subscribeToGoogleSignInOrigin,
    isGoogleSignInOriginSupported,
    () => null,
  );
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState("");
  const [resetMode, setResetMode] = useState(false);
  const [resetCode, setResetCode] = useState("");
  const [newPassword, setNewPassword] = useState("");
  const [resetStatus, setResetStatus] = useState("");
  const signInWithGoogle = useCallback(
    async (response: GoogleCredentialResponse) => {
      if (!response.credential) {
        setError(t("auth.googleNoCredential"));
        return;
      }
      setLoading(true);
      setError("");
      try {
        await authRequest<AuthPayload>(
          "/api/v1/users/google-login",
          {
            method: "POST",
            body: JSON.stringify({ credential: response.credential }),
          },
          t("auth.googleFailed"),
        );
        // The authentication cookie becomes available on the next request. Fetch the
        // authenticated session so its CSRF token is bound to the signed-in identity.
        onAuthenticated(await getCurrentUser());
      } catch (reason) {
        setError(
          reason instanceof Error
            ? reason.message
            : t("auth.googleFailed"),
        );
      } finally {
        setLoading(false);
      }
    },
    [onAuthenticated, t],
  );

  useEffect(() => {
    const clientId = options.googleClientId;
    const buttonHost = googleButtonRef.current;
    if (!options.googleEnabled || !clientId || !googleOriginSupported || !buttonHost) return;
    let active = true;
    let renderedWidth = 0;
    const render = () => {
      const width = Math.min(360, Math.floor(buttonHost.clientWidth));
      if (!active || !window.google || width < 200 || width === renderedWidth)
        return;
      buttonHost.replaceChildren();
      window.google.accounts.id.initialize({
        client_id: clientId,
        callback: signInWithGoogle,
      });
      window.google.accounts.id.renderButton(buttonHost, {
        theme: "outline",
        size: "large",
        type: "standard",
        shape: "rectangular",
        text: "continue_with",
        width: width.toString(),
      });
      renderedWidth = width;
      setGoogleRendered(true);
    };
    const resizeObserver = new ResizeObserver(render);
    resizeObserver.observe(buttonHost);
    const existing = document.getElementById(
      googleScriptId,
    ) as HTMLScriptElement | null;
    if (existing) {
      if (window.google) render();
      else existing.addEventListener("load", render, { once: true });
      return () => {
        active = false;
        resizeObserver.disconnect();
        existing.removeEventListener("load", render);
      };
    }
    const script = document.createElement("script");
    script.id = googleScriptId;
    script.src = "https://accounts.google.com/gsi/client";
    script.async = true;
    script.defer = true;
    script.addEventListener("load", render, { once: true });
    script.addEventListener("error", () => {
      if (active) setError(t("auth.googleUnavailable"));
    });
    document.head.appendChild(script);
    return () => {
      active = false;
      resizeObserver.disconnect();
      script.removeEventListener("load", render);
    };
  }, [
    locale,
    options.googleClientId,
    options.googleEnabled,
    googleOriginSupported,
    signInWithGoogle,
    t,
  ]);

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setLoading(true);
    setError("");
    try {
      await authRequest<AuthPayload>(
        "/api/v1/users/login",
        { method: "POST", body: JSON.stringify({ email, password }) },
        t("auth.passwordFailed"),
      );
      // See the Google path above: use a token issued after the session cookie is active.
      onAuthenticated(await getCurrentUser());
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : t("auth.passwordFailed"));
    } finally {
      setLoading(false);
    }
  }

  async function submitReset(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setLoading(true);
    setError("");
    try {
      await resetPassword(resetCode, newPassword);
      setResetStatus(t("auth.passwordSaved"));
      setResetMode(false);
      setResetCode("");
      setNewPassword("");
    } catch (reason) {
      setError(
        reason instanceof Error ? reason.message : t("auth.resetFailed"),
      );
    } finally {
      setLoading(false);
    }
  }

  const googleConfigured = options.googleEnabled && Boolean(options.googleClientId);
  const googleAvailable = googleConfigured && googleOriginSupported === true;
  const googleUnavailableAtThisAddress =
    googleConfigured && googleOriginSupported === false;
  const hasSignInMethod =
    googleAvailable ||
    options.passwordEnabled ||
    (googleOriginSupported === null && googleConfigured);

  return (
    <main className="auth-page">
      <PreferenceControls className="auth-preferences" />
      <section className="auth-card" aria-labelledby="login-title">
        <div className="auth-mark">
          <ShieldCheck />
        </div>
        <p className="auth-eyebrow">{t("auth.privateLibrary")}</p>
        <h1 id="login-title">{t("auth.welcome")}</h1>
        <p className="auth-copy">{t("auth.intro")}</p>
        {error && (
          <p className="auth-error" role="alert">
            {error}
          </p>
        )}
        {resetStatus && <p className="auth-success">{resetStatus}</p>}
        {googleAvailable ? (
          <div className="google-button-wrap">
            {!googleRendered && (
              <button className="auth-secondary" disabled type="button">
                {t("auth.continueGoogle")}
              </button>
            )}
            <div
              ref={googleButtonRef}
              className={googleRendered ? "" : "auth-google-hidden"}
            />
          </div>
        ) : null}
        {googleUnavailableAtThisAddress && (
          <p className="auth-footnote" role="status">
            {t("auth.googleNeedsFamilyLink")}
          </p>
        )}
        {googleAvailable && options.passwordEnabled && (
          <div className="auth-divider">
            <span />
            {t("auth.passwordDivider")}
            <span />
          </div>
        )}
        {options.passwordEnabled && !resetMode && <form className="auth-form" onSubmit={submit}>
          <label>
            {t("auth.email")}
            <input
              autoComplete="email"
              required
              type="email"
              value={email}
              onChange={(event) => setEmail(event.target.value)}
            />
          </label>
          <label>
            {t("auth.password")}
            <input
              autoComplete="current-password"
              minLength={12}
              required
              type="password"
              value={password}
              onChange={(event) => setPassword(event.target.value)}
            />
          </label>
          <button className="auth-primary" disabled={loading} type="submit">
            {loading ? <Loader2 className="spin" /> : <LogIn />}
            {loading ? t("auth.signingIn") : t("auth.signIn")}
          </button>
        </form>}
        {options.passwordEnabled && resetMode && (
          <form className="auth-form" onSubmit={submitReset}>
            <label>
              {t("auth.resetCode")}
              <textarea
                autoComplete="off"
                required
                value={resetCode}
                onChange={(event) => setResetCode(event.target.value)}
              />
            </label>
            <label>
              {t("auth.newPassword")}
              <input
                autoComplete="new-password"
                minLength={12}
                required
                type="password"
                value={newPassword}
                onChange={(event) => setNewPassword(event.target.value)}
              />
            </label>
            <button className="auth-primary" disabled={loading} type="submit">
              <KeyRound /> {t("auth.choosePassword")}
            </button>
          </form>
        )}
        {options.passwordEnabled && (
          <button
            className="auth-link-button"
            onClick={() => {
              setResetMode(!resetMode);
              setError("");
            }}
            type="button"
          >
            {resetMode ? t("auth.backToSignIn") : t("auth.haveRecoveryCode")}
          </button>
        )}
        {!hasSignInMethod && !googleUnavailableAtThisAddress && (
          <div className="auth-unavailable" role="status">
            <strong>
              {optionsUnavailable
                ? t("auth.optionsUnavailable")
                : t("auth.notConfigured")}
            </strong>
            <p>
              {optionsUnavailable
                ? t("auth.checkConnection")
                : t("auth.ownerEnableSignIn")}
            </p>
            {optionsUnavailable && (
              <button
                className="auth-secondary"
                disabled={optionsRefreshing}
                onClick={onRetryOptions}
                type="button"
              >
                {optionsRefreshing ? (
                  <Loader2 className="spin" />
                ) : (
                  <RefreshCw />
                )}
                {optionsRefreshing ? t("auth.checking") : t("auth.tryAgain")}
              </button>
            )}
          </div>
        )}
        {hasSignInMethod && (
          <p className="auth-footnote">
            {t("auth.allowlistNotice")}
          </p>
        )}
        <Link className="auth-return-link" href="/">
          <ArrowLeft /> {t("auth.backHome")}
        </Link>
      </section>
    </main>
  );
}

function AccountDialog({
  user,
  onClose,
  onLogout,
  onPasswordChanged,
  forced,
}: {
  user: CurrentUser;
  onClose: () => void;
  onLogout: () => void;
  onPasswordChanged: () => void;
  forced: boolean;
}) {
  const { t } = usePreferences();
  const [currentPassword, setCurrentPassword] = useState("");
  const [password, setPassword] = useState("");
  const [showCurrentPassword, setShowCurrentPassword] = useState(false);
  const [showPassword, setShowPassword] = useState(false);
  const [status, setStatus] = useState("");
  const [error, setError] = useState("");
  const [saving, setSaving] = useState(false);
  const passwordRequirements = [
    { met: password.length >= 12, label: t("account.passwordLength") },
    { met: /\p{Lu}/u.test(password), label: t("account.passwordUppercase") },
    { met: /\p{Ll}/u.test(password), label: t("account.passwordLowercase") },
    { met: /\p{Nd}/u.test(password), label: t("account.passwordNumber") },
    {
      met: Array.from(password).some((character) =>
        /[^\p{L}\p{N}]/u.test(character),
      ),
      label: t("account.passwordSpecial"),
    },
  ];
  const passwordIsValid = passwordRequirements.every(({ met }) => met);
  const currentPasswordIsValid =
    !user.hasPassword || user.mustChangePassword || currentPassword.length > 0;
  async function savePassword(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setSaving(true);
    setStatus("");
    setError("");
    try {
      const updatedUser = await authRequest<CurrentUser>(
        "/api/v1/users/set-password",
        {
          method: "POST",
          body: JSON.stringify(
            user.hasPassword ? { currentPassword, password } : { password },
          ),
        },
        t("account.saveFailed"),
      );
      void updatedUser;
      setPassword("");
      setCurrentPassword("");
      setStatus(t("account.saved"));
      resetAuthenticationState();
      window.setTimeout(onPasswordChanged, 1200);
    } catch (reason) {
      setError(
        reason instanceof Error
          ? reason.message
          : t("account.saveFailed"),
      );
    } finally {
      setSaving(false);
    }
  }
  async function logout() {
    try {
      await authRequest("/api/v1/users/logout", { method: "POST" });
    } finally {
      resetAuthenticationState();
      onLogout();
    }
  }
  return (
    <div
      className="account-backdrop"
      onMouseDown={forced ? undefined : onClose}
    >
      <section
        className="account-dialog"
        aria-modal="true"
        aria-labelledby="account-title"
        role="dialog"
        onMouseDown={(event) => event.stopPropagation()}
      >
        {!forced && <button
          className="account-close"
          aria-label={t("account.close")}
          onClick={onClose}
          type="button"
        >
          ×
        </button>}
        <p className="auth-eyebrow">{t("account.eyebrow")}</p>
        <h2 id="account-title">{user.displayName}</h2>
        <p className="account-email">{user.email}</p>
        {user.passwordLoginAllowed && <form className="auth-form account-form" onSubmit={savePassword}>
          <div>
            <KeyRound />
            <strong>
              {user.hasPassword
                ? t("account.changePassword")
                : t("account.addPassword")}
            </strong>
          </div>
          <p>
            {forced
              ? t("account.forcedHelp")
              : user.hasPassword
              ? t("account.changeHelp")
              : t("account.addHelp")}
          </p>
          {user.hasPassword && !user.mustChangePassword && (
            <div className="password-control">
              <label htmlFor="account-current-password">
                {t("account.currentPassword")}
              </label>
              <span className="password-field">
                <input
                  autoComplete="current-password"
                  autoFocus
                  id="account-current-password"
                  required
                  type={showCurrentPassword ? "text" : "password"}
                  value={currentPassword}
                  onChange={(event) => setCurrentPassword(event.target.value)}
                />
                <button
                  aria-label={
                    showCurrentPassword
                      ? t("account.hideCurrentPassword")
                      : t("account.showCurrentPassword")
                  }
                  aria-pressed={showCurrentPassword}
                  className="password-visibility"
                  onClick={() => setShowCurrentPassword((visible) => !visible)}
                  type="button"
                >
                  {showCurrentPassword ? <EyeOff /> : <Eye />}
                </button>
              </span>
            </div>
          )}
          <div className="password-control">
            <label htmlFor="account-new-password">
              {t("account.newPassword")}
            </label>
            <span className="password-field">
              <input
                aria-describedby="account-password-requirements"
                autoComplete="new-password"
                autoFocus={!user.hasPassword || user.mustChangePassword}
                id="account-new-password"
                minLength={12}
                required
                type={showPassword ? "text" : "password"}
                value={password}
                onChange={(event) => setPassword(event.target.value)}
              />
              <button
                aria-label={
                  showPassword
                    ? t("account.hidePassword")
                    : t("account.showPassword")
                }
                aria-pressed={showPassword}
                className="password-visibility"
                onClick={() => setShowPassword((visible) => !visible)}
                type="button"
              >
                {showPassword ? <EyeOff /> : <Eye />}
              </button>
            </span>
          </div>
          <div
            className="password-requirements"
            id="account-password-requirements"
          >
            <span>{t("account.passwordRequirements")}</span>
            <ul>
              {passwordRequirements.map(({ label, met }) => (
                <li className={met ? "is-met" : undefined} key={label}>
                  <Check aria-hidden="true" />
                  {label}
                </li>
              ))}
            </ul>
          </div>
          {status && (
            <p className="auth-success" role="status">
              {status}
            </p>
          )}
          {error && (
            <p className="auth-error" role="alert">
              {error}
            </p>
          )}
          <button
            className="auth-primary"
            disabled={saving || !passwordIsValid || !currentPasswordIsValid}
            type="submit"
          >
            {saving ? <Loader2 className="spin" /> : <KeyRound />}
            {saving ? t("account.saving") : t("account.save")}
          </button>
        </form>}
        <button className="auth-signout" onClick={logout} type="button">
          <LogOut /> {t("topbar.signOut")}
        </button>
      </section>
    </div>
  );
}
