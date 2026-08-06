import type { MediaItem } from "@/lib/types";

export type PreviewKind = "media" | "audio" | "text" | "external";

export function previewKind(item: MediaItem): PreviewKind | undefined {
  if (item.kind === "image" || item.kind === "video") return "media";
  const mimeType = item.mimeType?.toLowerCase() ?? "";
  if (mimeType.startsWith("audio/")) return "audio";
  if (mimeType === "application/pdf") return "external";
  if (
    mimeType.startsWith("text/") ||
    mimeType === "application/json" ||
    mimeType === "application/xml" ||
    mimeType.endsWith("+json") ||
    mimeType.endsWith("+xml")
  )
    return "text";
  return undefined;
}

export function isPreviewable(item: MediaItem) {
  return previewKind(item) !== undefined;
}
