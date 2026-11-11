export type ItemKind = "folder" | "image" | "video" | "document";
export type LocalState = "local" | "cached" | "remote" | "pinned";
export type LibraryView = "photos" | "files" | "shared" | "albums" | "trash";
export type PhotoScope = "all" | "folder";
export type FileDiscoveryScope = "all" | "subtree" | "shared";
export type MediaTypeFilter = "all" | "image" | "video";
export type FileTypeFilter =
  "all" | "folder" | "document" | "pdf" | "spreadsheet" | "image" | "video";
export type ItemSort =
  | "name_asc"
  | "name_desc"
  | "modified_desc"
  | "modified_asc"
  | "size_desc"
  | "size_asc";
export type PhotoSort = "captured_desc" | "captured_asc" | "added_desc";

export interface ItemCapabilities {
  canRename: boolean;
  canMove: boolean;
  canTrash: boolean;
  canFavorite?: boolean;
  canRetryMetadata?: boolean;
  canShare?: boolean;
}

export interface MediaItem {
  id: string;
  name: string;
  parentId?: string;
  kind: ItemKind;
  mimeType?: string;
  sizeBytes?: number;
  createdAt: string;
  modifiedAt: string;
  capturedAt?: string;
  captureDateSource?: string;
  metadata?: MediaMetadata;
  favorite: boolean;
  localState: LocalState;
  syncState: "synced" | "pending" | "error";
  childCount?: number;
  thumbnailUrl?: string;
  catalogPath?: string;
  trashedAt?: string;
  systemRole?: "media_root" | "documents_root";
  accessRole?: "owner" | "viewer";
  sharedByDisplayName?: string;
  sharedAt?: string;
  tags?: TagSummary[];
  capabilities: ItemCapabilities;
}

export interface TagSummary {
  id: string;
  name: string;
  color:
    "gray" | "red" | "orange" | "yellow" | "green" | "blue" | "purple" | "pink";
}

export interface CatalogTag extends TagSummary {
  fileCount: number;
  folderCount: number;
}

export interface ShareRecipient {
  userId: string;
  displayName: string;
  email: string;
}

export interface ItemShare {
  recipientUserId: string;
  recipientDisplayName: string;
  recipientEmail: string;
  role: "viewer";
  sharedAt: string;
}

export type AlbumShareRole = "viewer" | "collaborator";
export type AlbumShare = Omit<ItemShare, "role"> & { role: AlbumShareRole };

export interface Album {
  id: string;
  name: string;
  description?: string;
  memberCount: number;
  coverFileId?: string;
  coverFileIds: string[];
  createdAt: string;
  modifiedAt: string;
  accessRole: "owner" | AlbumShareRole;
  sharedByDisplayName?: string;
  sharedAt?: string;
}

export interface AlbumDetails {
  album: Album;
  items: MediaItem[];
  availableYears: number[];
  nextCursor?: string;
}

export interface AlbumItemFilters {
  query?: string;
  type: MediaTypeFilter;
  favorite: boolean;
  sort: PhotoSort;
  year?: number;
  tagId?: string;
}

export interface MediaMetadata {
  state: "pending" | "processing" | "succeeded" | "failed";
  attemptCount: number;
  errorCode?: string;
  errorMessage?: string;
  detectedMimeType?: string;
  mediaFormat?: string;
  width?: number;
  height?: number;
  orientation?: number;
  embeddedCapturedAt?: string;
  filenameCapturedAt?: string;
  importedCapturedAt?: string;
  description?: string;
  latitude?: number;
  longitude?: number;
  altitudeMeters?: number;
  processedAt?: string;
  recoveryManifestState:
    "pending" | "processing" | "succeeded" | "failed" | "disabled";
}

export interface CatalogPage {
  items: MediaItem[];
  nextCursor?: string;
  previousCursor?: string;
}

export interface PhotoTimelineAnchor {
  year: number;
  month: number;
  count: number;
  cursor: string;
}

export interface PhotoTimelineAnchors {
  anchors: PhotoTimelineAnchor[];
  totalCount: number;
}

export interface TimelineEvent {
  id: string;
  title: string;
  startDate: string;
  endDate?: string;
  description?: string;
  createdAt: string;
  modifiedAt: string;
}
