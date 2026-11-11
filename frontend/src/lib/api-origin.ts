const configuredApiOrigin =
  process.env.NEXT_PUBLIC_API_BASE_URL?.replace(/\/$/, "") ?? "";
const sameOriginDeployment =
  process.env.NEXT_PUBLIC_API_SAME_ORIGIN === "true";

export const apiEnabled = sameOriginDeployment || Boolean(configuredApiOrigin);

// The Docker image serves the frontend and API from one origin. A separate
// development frontend points at the API with NEXT_PUBLIC_API_BASE_URL.
export function apiUrl(path: string) {
  if (sameOriginDeployment) return path;
  return `${configuredApiOrigin}${path}`;
}
