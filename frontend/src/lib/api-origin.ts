const configuredApiOrigin =
  process.env.NEXT_PUBLIC_API_BASE_URL?.replace(/\/$/, "") ?? "";

export const apiEnabled = true;

export function apiUrl(path: string) {
  return `${configuredApiOrigin}${path}`;
}
