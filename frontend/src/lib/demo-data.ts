import type { MediaItem } from "./types";

const defaults = {
  createdAt: "2026-07-01T12:00:00Z",
  modifiedAt: "2026-07-16T12:00:00Z",
  favorite: false,
  localState: "remote" as const,
  syncState: "synced" as const,
  capabilities: { canRename: true, canMove: true, canTrash: true },
};

export const items: MediaItem[] = [
  {
    ...defaults,
    id: "media",
    name: "Media",
    kind: "folder",
    systemRole: "media_root",
    capabilities: { canRename: false, canMove: false, canTrash: false },
  },
  {
    ...defaults,
    id: "documents",
    name: "Documents",
    kind: "folder",
    systemRole: "documents_root",
    capabilities: { canRename: false, canMove: false, canTrash: false },
  },
  {
    ...defaults,
    id: "p1",
    name: "Sunday at the lake.jpg",
    kind: "image",
    mimeType: "image/jpeg",
    sizeBytes: 4_800_000,
    capturedAt: "2026-07-06T14:30:00Z",
    favorite: true,
    thumbnailUrl:
      "https://images.unsplash.com/photo-1500530855697-b586d89ba3ee?auto=format&fit=crop&w=900&q=82",
  },
  {
    ...defaults,
    id: "p2",
    name: "Garden afternoon.jpg",
    kind: "image",
    mimeType: "image/jpeg",
    sizeBytes: 3_200_000,
    capturedAt: "2026-07-05T16:00:00Z",
    thumbnailUrl:
      "https://images.unsplash.com/photo-1497250681960-ef046c08a56e?auto=format&fit=crop&w=900&q=82",
  },
  {
    ...defaults,
    id: "p3",
    name: "Boardwalk.mov",
    kind: "video",
    mimeType: "video/quicktime",
    sizeBytes: 128_000_000,
    capturedAt: "2026-07-04T18:00:00Z",
  },
];
