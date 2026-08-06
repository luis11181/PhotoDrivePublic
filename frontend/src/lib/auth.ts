import { logError, logInfo } from "./logger";
import { apiUrl } from "./api-origin";

export { apiEnabled } from "./api-origin";

export type CurrentUser = {
  id: string;
  email: string;
  displayName: string;
  hasPassword: boolean;
  emailVerified: boolean;
  isOwner: boolean;
  googleLoginAllowed: boolean;
  passwordLoginAllowed: boolean;
  mustChangePassword: boolean;
};

export type AuthPayload = {
  user: CurrentUser;
  expiresAt: string;
};

type ApiEnvelope<T> = {
  successful: boolean;
  data?: T;
  error?: { detail?: string; title?: string };
};

export class ApiRequestError extends Error {
  constructor(
    message: string,
    readonly status: number,
  ) {
    super(message);
    this.name = "ApiRequestError";
  }
}

let csrfToken = "";
let refreshPromise: Promise<boolean> | null = null;

export const authSessionExpiredEvent = "media-explorer:auth-session-expired";

export type SignInOptions = {
  googleEnabled: boolean;
  passwordEnabled: boolean;
  googleClientId?: string;
};

export type ManagedUser = {
  id: string;
  email: string;
  displayName: string;
  role: "OWNER" | "MEMBER";
  status: "Invited" | "Active" | "Disabled";
  googleAllowed: boolean;
  passwordAllowed: boolean;
  googleLinked: boolean;
  hasPassword: boolean;
  mustChangePassword: boolean;
  activeSessionCount: number;
};

export type IssuedResetCode = { code: string; expiresAt?: string };

async function readBody(response: Response) {
  const text = await response.text();
  if (!text) return null;
  try {
    return JSON.parse(text) as ApiEnvelope<unknown>;
  } catch {
    return null;
  }
}

export async function authRequest<T>(
  path: string,
  init?: RequestInit,
  fallback = "Request failed",
) {
  let result = await sendRequest(path, init);
  if (
    result.response.status === 401 &&
    shouldAttemptSessionRefresh(path) &&
    (await refreshSession())
  ) {
    result = await sendRequest(path, init);
  }

  const { response, body, requestId, operationPath, method, startedAt } = result;
  const responseRequestId =
    response.headers.get("X-Correlation-ID") ?? requestId;
  if (response.ok && response.status === 204) {
    logInfo("frontend.api", "request completed", {
      method,
      path: operationPath,
      status: response.status,
      requestId: responseRequestId,
      elapsedMs: Date.now() - startedAt,
    });
    return undefined as T;
  }
  if (!response.ok || !body?.successful) {
    logError("frontend.api", "request completed with error", {
      method,
      path: operationPath,
      status: response.status,
      requestId: responseRequestId,
      elapsedMs: Date.now() - startedAt,
    });
    if (response.status === 401 && shouldAttemptSessionRefresh(path)) {
      clearAuthenticationState();
    }
    throw new ApiRequestError(
      body?.error?.detail || body?.error?.title || fallback,
      response.status,
    );
  }
  logInfo("frontend.api", "request completed", {
    method,
    path: operationPath,
    status: response.status,
    requestId: responseRequestId,
    elapsedMs: Date.now() - startedAt,
  });
  if (operationPath === "/api/v1/users/logout") resetAuthenticationState();
  return body.data as T;
}

async function sendRequest(path: string, init?: RequestInit) {
  const headers = new Headers(init?.headers);
  const method = init?.method?.toUpperCase() ?? "GET";
  const requestId = headers.get("X-Correlation-ID") ?? createRequestId();
  const operationPath = path.split("?", 1)[0];
  const startedAt = Date.now();
  headers.set("X-Correlation-ID", requestId);
  if (init?.body && !headers.has("Content-Type"))
    headers.set("Content-Type", "application/json");
  if (
    init?.method &&
    !["GET", "HEAD", "OPTIONS"].includes(init.method.toUpperCase()) &&
    csrfToken
  ) {
    headers.set("X-CSRF-Token", csrfToken);
  }
  logInfo("frontend.api", "request started", {
    method,
    path: operationPath,
    requestId,
  });
  let response: Response;
  try {
    response = await fetch(apiUrl(path), {
      ...init,
      cache: "no-store",
      credentials: "include",
      headers,
    });
  } catch (reason) {
    if (isAbortError(reason)) {
      logInfo("frontend.api", "request cancelled", {
        method,
        path: operationPath,
        requestId,
        elapsedMs: Date.now() - startedAt,
      });
      throw reason;
    }
    logError("frontend.api", "request failed before response", {
      method,
      path: operationPath,
      requestId,
      elapsedMs: Date.now() - startedAt,
      reason: reason instanceof Error ? reason.message : "unknown_error",
    });
    throw reason;
  }
  const nextCsrfToken = response.headers.get("X-CSRF-Token");
  if (nextCsrfToken) csrfToken = nextCsrfToken;
  const body = await readBody(response);
  return { response, body, requestId, operationPath, method, startedAt };
}

function shouldAttemptSessionRefresh(path: string) {
  const operationPath = path.split("?", 1)[0];
  return ![
    "/api/v1/users/login",
    "/api/v1/users/google-login",
    "/api/v1/users/reset-password",
    "/api/v1/users/sign-in-options",
    "/api/v1/users/csrf-token",
    "/api/v1/users/refresh-token",
  ].includes(operationPath);
}

function refreshSession() {
  if (!refreshPromise) {
    refreshPromise = coordinateSessionRefresh().finally(() => {
      refreshPromise = null;
    });
  }
  return refreshPromise;
}

async function coordinateSessionRefresh() {
  const refresh = async () => {
    // Another tab may have already rotated the shared cookies while this tab waited.
    const currentSession = await sendRequest("/api/v1/users/me");
    if (currentSession.response.ok && currentSession.body?.successful) return true;

    const antiforgery = await sendRequest("/api/v1/users/csrf-token");
    if (!antiforgery.response.ok) return false;

    const renewed = await sendRequest("/api/v1/users/refresh-token", {
      method: "POST",
    });
    if (!renewed.response.ok || !renewed.body?.successful) return false;

    // Bind a fresh CSRF request token to the renewed authenticated identity.
    const restored = await sendRequest("/api/v1/users/me");
    return restored.response.ok && restored.body?.successful === true;
  };

  try {
    if (typeof navigator !== "undefined" && navigator.locks) {
      return await navigator.locks.request(
        `media-explorer:${apiUrl("")}:refresh`,
        refresh,
      );
    }
    return await refresh();
  } catch (reason) {
    if (isAbortError(reason)) throw reason;
    logError("frontend.auth", "session refresh failed", {
      reason: reason instanceof Error ? reason.message : "unknown_error",
    });
    return false;
  }
}

export function resetAuthenticationState() {
  csrfToken = "";
}

function clearAuthenticationState() {
  resetAuthenticationState();
  if (typeof window !== "undefined") {
    window.dispatchEvent(new Event(authSessionExpiredEvent));
  }
}

function createRequestId() {
  return typeof crypto !== "undefined" && "randomUUID" in crypto
    ? crypto.randomUUID()
    : `${Date.now()}-${Math.random().toString(16).slice(2)}`;
}

function isAbortError(reason: unknown) {
  return reason instanceof DOMException && reason.name === "AbortError";
}

export function getCurrentUser() {
  return authRequest<CurrentUser>(
    "/api/v1/users/me",
    undefined,
    "Could not restore your session",
  );
}

export function getSignInOptions() {
  return authRequest<SignInOptions>(
    "/api/v1/users/sign-in-options",
    undefined,
    "Could not load sign-in options",
  );
}

export function listManagedUsers() {
  return authRequest<ManagedUser[]>("/api/v1/users/admin");
}

export function inviteManagedUser(request: {
  email: string;
  displayName: string;
  role: "OWNER" | "MEMBER";
  googleAllowed: boolean;
  passwordAllowed: boolean;
}) {
  return authRequest<{
    user: ManagedUser;
    passwordSetup?: IssuedResetCode;
  }>("/api/v1/users/admin", {
    method: "POST",
    body: JSON.stringify(request),
  });
}

export function updateManagedUser(
  userId: string,
  request: {
    displayName: string;
    role: "OWNER" | "MEMBER";
    status: "Invited" | "Active" | "Disabled";
    googleAllowed: boolean;
    passwordAllowed: boolean;
  },
) {
  return authRequest<ManagedUser>(`/api/v1/users/admin/${userId}`, {
    method: "PATCH",
    body: JSON.stringify(request),
  });
}

export function issuePasswordReset(userId: string) {
  return authRequest<IssuedResetCode>(
    `/api/v1/users/admin/${userId}/password-reset`,
    { method: "POST" },
  );
}

export function revokeUserSessions(userId: string) {
  return authRequest(`/api/v1/users/admin/${userId}/revoke-sessions`, {
    method: "POST",
  });
}

export function resetPassword(code: string, password: string) {
  return authRequest("/api/v1/users/reset-password", {
    method: "POST",
    body: JSON.stringify({ code, password }),
  });
}
