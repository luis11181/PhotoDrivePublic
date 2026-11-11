import { apiEnabled, authRequest } from "./auth";
import { apiUrl } from "./api-origin";
import type {
  CatalogPage,
  FileTypeFilter,
  MediaItem,
  MediaTypeFilter,
  ItemSort,
  PhotoSort,
  PhotoScope,
  PhotoTimelineAnchors,
  ShareRecipient,
  ItemShare,
  AlbumShare,
  AlbumShareRole,
  Album,
  AlbumDetails,
  AlbumItemFilters,
  CatalogTag,
  TimelineEvent,
} from "./types";

type CatalogItemWire = Omit<MediaItem, "thumbnailUrl"> & {
  thumbnailPath?: string;
};
type CatalogPageWire = {
  items: CatalogItemWire[];
  nextCursor?: string;
  previousCursor?: string;
};

export type UploadSessionResponse = {
  id: string;
  chunkSizeBytes: number;
  receivedBytes: number;
  sizeBytes?: number;
  state?: "pending" | "completed";
};

type UploadProgressResponse = {
  receivedBytes: number;
  sizeBytes: number;
};

type CompleteUploadResponse = {
  item: CatalogItemWire;
  duplicateState: "none" | "active" | "trashed";
};

export type UploadDestination = {
  albumId?: string;
  albumName?: string;
  parentId?: string;
  useDefaultMediaFolder?: boolean;
  useDefaultDocumentsFolder?: boolean;
  allowNameConflict?: boolean;
};

export type DeveloperMaintenanceReport = {
  ranAt: string;
  catalogFileCount: number;
  catalogFolderCount: number;
  referencedBlobCount: number;
  referencedBlobBytes: number;
  missingOriginalCount: number;
  orphanedCatalogBlobCount: number;
  orphanedCatalogBlobBytes: number;
  pendingPermanentDeletionBlobCount: number;
  pendingPermanentDeletionBytes: number;
  untrackedS3OriginalCount: number;
  pendingCleanupCandidateCount: number;
  removedCompletedStagingFiles: number;
  removedCompletedStagingBytes: number;
  originalCacheEntryCount: number;
  originalCacheBytes: number;
  originalCacheLimitBytes: number;
  thumbnailCacheEntryCount: number;
  thumbnailCacheBytes: number;
  evictedCacheEntries: number;
  evictedCacheBytes: number;
  s3OriginalKeyCount: number;
  s3OriginalVersionCount: number;
  s3OriginalVersionBytes: number;
  s3RecoveryManifestKeyCount: number;
  s3RecoveryManifestVersionCount: number;
  s3RecoveryManifestVersionBytes: number;
  s3DeleteMarkerCount: number;
  cleanupCandidates: Array<{
    id: string;
    kind: string;
    sizeBytes: number;
    detectedAt: string;
    attemptCount: number;
    error?: string;
  }>;
  issues: Array<{
    severity: string;
    code: string;
    message: string;
  }>;
};

export type DeveloperBackupReport = {
  ranAt: string;
  libraryCount: number;
  folderCount: number;
  fileCount: number;
  blobCount: number;
  fileVersionCount: number;
  fileVersionMetadataCount: number;
  remoteBackupSetId?: string;
  remoteBackupArtifactCount: number;
  remoteBackupBytes: number;
  remoteBackupVerified: boolean;
  issues: Array<{
    severity: string;
    code: string;
    message: string;
  }>;
};

export type DeveloperBackupStatus = {
  lastRemoteVerifiedAt?: string;
  ageHours?: number;
  freshnessWindowHours: number;
  state: "current" | "stale" | "missing";
};

export type DeveloperOperationalHealth = {
  checkedAt: string;
  state: "healthy" | "attention";
  lastRemoteVerifiedBackupAt?: string;
  lastBackupFailureAt?: string;
  consecutiveBackupFailureCount: number;
  pendingUploadCount: number;
  staleUploadCount: number;
  pendingMetadataCount: number;
  staleMetadataCount: number;
  failedMetadataCount: number;
  repeatedWorkerFailureCount: number;
  availableDiskBytes: number;
  originalCacheBytes: number;
  originalCacheMaxBytes: number;
  lowDiskSpace: boolean;
  cachePressure: boolean;
  isPrimaryHost: boolean;
  databaseLastPullAt?: string;
  databaseLastCloudFailureAt?: string;
  issues: DeveloperMaintenanceReport["issues"];
};

export type ClusterHost = {
  id: string;
  name: string;
  firstSeenAt: string;
  lastSeenAt: string;
  online: boolean;
  isCurrent: boolean;
  isPrimary: boolean;
};

export type ClusterHosts = {
  currentHostId: string;
  primaryHostId?: string;
  primaryChangedAt?: string;
  hosts: ClusterHost[];
};

export type CleanupStorageInconsistenciesResult = {
  safetyBackupSetId?: string;
  deletedCandidateCount: number;
  resolvedCandidateCount: number;
  failedCandidateCount: number;
  remainingCandidates: DeveloperMaintenanceReport["cleanupCandidates"];
};

export type BatchItemResult = {
  itemId: string;
  status: "succeeded" | "unchanged" | "skipped" | "failed";
  errorCode?: string;
  message?: string;
};

export type BatchItemsResult = {
  requestedCount: number;
  succeededCount: number;
  unchangedCount: number;
  skippedCount: number;
  failedCount: number;
  results: BatchItemResult[];
};

export type CacheBuildOptions = {
  originalCacheMaxBytes: number;
  originalCacheBytes: number;
  originalCacheEntryCount: number;
  availableDiskBytes: number;
  catalogFileCount: number;
  storedOriginalBytes: number;
  catalogMediaItemCount: number;
  distinctOriginalCount: number;
  gridThumbnailCount: number;
  cachedOriginalCount: number;
};

export type CacheBuildRequest = {
  keepOriginals: boolean;
};

export type CacheBuildPreview = {
  selectedItemCount: number;
  queuedItemCount: number;
  missingThumbnailCount: number;
  missingOriginalCount: number;
  downloadBytes: number;
  keepOriginals: boolean;
  originalCacheMaxBytes: number;
  originalCacheBytes: number;
  availableDiskBytes: number;
  fitsConfiguredCache: boolean;
};

export type CacheBuildJob = {
  id: string;
  keepOriginals: boolean;
  state:
    | "pending"
    | "running"
    | "cancelling"
    | "cancelled"
    | "completed"
    | "completed_with_errors";
  selectedItemCount: number;
  queuedItemCount: number;
  pendingCount: number;
  processingCount: number;
  completedCount: number;
  skippedCount: number;
  failedCount: number;
  cancelledCount: number;
  plannedDownloadBytes: number;
  error?: string;
  createdAt: string;
  startedAt?: string;
  completedAt?: string;
};

export type ClearTrashResult = {
  deletedFileCount: number;
  deletedFolderCount: number;
  deletedOriginalCount: number;
  deletedOriginalBytes: number;
  failedOriginalCount: number;
};

export type TrashSummary = {
  fileCount: number;
  folderCount: number;
  originalCount: number;
  originalBytes: number;
};

export type TrashPreview = {
  selectedRootCount: number;
  fileCount: number;
  folderCount: number;
  photoItemCount: number;
  albumCount: number;
};

export type StorageSummary = {
  catalogFileCount: number;
  uniqueOriginalCount: number;
  storedOriginalBytes: number;
  reclaimableTrashOriginalCount: number;
  reclaimableTrashBytes: number;
  calculatedAt: string;
};

export type ImportJob = {
  id: string;
  destinationFolderId?: string;
  destinationPath: string;
  sourceKind: string;
  nameConflictPolicy: "skip" | "keep_both";
  skipIdenticalFiles: boolean;
  state: "running" | "completed" | "completed_with_errors";
  discoveredCount: number;
  pendingCount: number;
  processingCount: number;
  importedCount: number;
  duplicateCount: number;
  nameConflictCount: number;
  failedCount: number;
  warningCount: number;
  orphanSidecarCount: number;
  ignoredJsonCount: number;
  outcomes: Record<string, number>;
  error?: string;
  createdAt: string;
  startedAt?: string;
  completedAt?: string;
};

export type ImportDestination = {
  id: string;
  name: string;
  path: string;
  systemRole?: "media_root" | "documents_root";
};

export type ImportItemCategory =
  | "all"
  | "imported"
  | "skipped"
  | "duplicates"
  | "name_conflicts"
  | "warnings"
  | "failed";

export type ImportItemResult = {
  id: string;
  catalogFileId?: string;
  name: string;
  relativePath: string;
  state: "pending" | "processing" | "imported" | "skipped" | "failed";
  outcomeCode?: string;
  warningCodes: string[];
  error?: string;
};

export type ImportItemResultsPage = {
  items: ImportItemResult[];
  nextCursor?: string;
};

export function getImportOptions() {
  return authRequest<{ destinations: ImportDestination[] }>(
    "/api/v1/imports/options",
    undefined,
    "Could not load import destinations",
  );
}

export type ImportSourceFolders = {
  path: string;
  parentPath: string | null;
  folders: { name: string; path: string }[];
  nextCursor?: string | null;
};

export function listImportSourceFolders(path: string, cursor?: string, signal?: AbortSignal) {
  const query = new URLSearchParams({ path, limit: "50" });
  if (cursor) query.set("cursor", cursor);
  return authRequest<ImportSourceFolders>(`/api/v1/imports/source-folders?${query}`, { signal }, "Could not load server import folders");
}

export function listImportJobs() {
  return authRequest<ImportJob[]>(
    "/api/v1/imports",
    undefined,
    "Could not load imports",
  );
}

export function startGoogleTakeoutImport(request: {
  sourcePath: string;
  destinationFolderId: string;
  nameConflictPolicy: "skip" | "keep_both";
  skipIdenticalFiles: boolean;
}) {
  return authRequest<ImportJob>(
    "/api/v1/imports/google-takeout",
    {
      method: "POST",
      body: JSON.stringify(request),
    },
    "Could not start the Takeout import",
  );
}

export function listImportItems(
  jobId: string,
  category: ImportItemCategory,
  cursor?: string,
  signal?: AbortSignal,
) {
  const query = new URLSearchParams({ category, limit: "50" });
  if (cursor) query.set("cursor", cursor);
  return authRequest<ImportItemResultsPage>(
    `/api/v1/imports/${jobId}/items?${query}`,
    { signal },
    "Could not load import item details",
  );
}

export function retryImportJob(jobId: string) {
  return authRequest<ImportJob>(
    `/api/v1/imports/${jobId}/retry`,
    { method: "POST" },
    "Could not retry failed import items",
  );
}

export function deleteImportJob(jobId: string) {
  return authRequest<void>(
    `/api/v1/imports/${jobId}`,
    { method: "DELETE" },
    "Could not remove the import",
  );
}

export async function queryFiles(options: {
  signal?: AbortSignal;
  query?: string;
  location: "children" | "subtree" | "all";
  folderId?: string;
  includePhotos?: boolean;
  favorite?: boolean;
  type: FileTypeFilter;
  sort: ItemSort;
  cursor?: string;
  tagId?: string;
}): Promise<CatalogPage> {
  const query = new URLSearchParams({
    limit: "60",
    location: options.location,
    type: options.type,
    sort: options.sort,
  });
  if (options.query) query.set("q", options.query);
  if (options.folderId) query.set("folderId", options.folderId);
  if (options.includePhotos) query.set("includePhotos", "true");
  if (options.favorite) query.set("favorite", "true");
  if (options.cursor) query.set("cursor", options.cursor);
  if (options.tagId) query.set("tagId", options.tagId);
  return mapPage(
    await authRequest<CatalogPageWire>(
      `/api/v1/items?${query}`,
      { signal: options.signal },
      "Could not load your files",
    ),
  );
}

export async function listTrashItems(options: {
  signal?: AbortSignal;
  cursor?: string;
  type: FileTypeFilter;
  sort: ItemSort;
}): Promise<CatalogPage> {
  const query = new URLSearchParams({
    limit: "60",
    type: options.type,
    sort: options.sort,
  });
  if (options.cursor) query.set("cursor", options.cursor);
  return mapPage(
    await authRequest<CatalogPageWire>(
      `/api/v1/items/trash?${query}`,
      { signal: options.signal },
      "Could not load Trash",
    ),
  );
}

export async function listFavoriteFolders(
  options: {
    signal?: AbortSignal;
    cursor?: string;
  } = {},
): Promise<CatalogPage> {
  const query = new URLSearchParams({ limit: "60" });
  if (options.cursor) query.set("cursor", options.cursor);
  return mapPage(
    await authRequest<CatalogPageWire>(
      `/api/v1/items/favorite-folders?${query}`,
      { signal: options.signal },
      "Could not load favorite folders",
    ),
  );
}

export async function listSharedItems(options: {
  signal?: AbortSignal;
  parentId?: string;
  cursor?: string;
  type?: FileTypeFilter;
  sort?: ItemSort;
}): Promise<CatalogPage> {
  const query = new URLSearchParams({ limit: "60" });
  if (options.parentId) query.set("parentId", options.parentId);
  if (options.cursor) query.set("cursor", options.cursor);
  if (options.type) query.set("type", options.type);
  if (options.sort) query.set("sort", options.sort);
  return mapPage(
    await authRequest<CatalogPageWire>(
      `/api/v1/shares?${query}`,
      { signal: options.signal },
      "Could not load items shared with you",
    ),
  );
}

export async function listShareRecipients(): Promise<ShareRecipient[]> {
  const response = await authRequest<{ recipients: ShareRecipient[] }>(
    "/api/v1/shares/recipients",
    undefined,
    "Could not load family members",
  );
  return response.recipients;
}

export async function listItemShares(itemId: string): Promise<ItemShare[]> {
  const response = await authRequest<{ shares: ItemShare[] }>(
    `/api/v1/shares/items/${itemId}`,
    undefined,
    "Could not load sharing access",
  );
  return response.shares;
}

export function shareItem(itemId: string, recipientUserId: string) {
  return authRequest<ItemShare>(
    `/api/v1/shares/items/${itemId}/${recipientUserId}`,
    { method: "PUT", body: JSON.stringify({ role: "viewer" }) },
    "Could not share this item",
  );
}

export function revokeItemShare(itemId: string, recipientUserId: string) {
  return authRequest<void>(
    `/api/v1/shares/items/${itemId}/${recipientUserId}`,
    { method: "DELETE" },
    "Could not revoke sharing access",
  );
}

export async function listAlbums(scope: "all" | "owned" | "shared" = "all") {
  const response = await authRequest<{ albums: Album[] }>(
    `/api/v1/albums?scope=${scope}`,
    undefined,
    "Could not load albums",
  );
  return response.albums;
}

export async function getAlbum(
  albumId: string,
  filters: AlbumItemFilters,
  cursor?: string,
  signal?: AbortSignal,
): Promise<AlbumDetails> {
  const queryParams = new URLSearchParams({
    limit: "60",
    type: filters.type,
    favorite: String(filters.favorite),
    sort: filters.sort,
  });
  if (filters.query) queryParams.set("query", filters.query);
  if (filters.year !== undefined) queryParams.set("year", String(filters.year));
  if (filters.tagId) queryParams.set("tagId", filters.tagId);
  if (cursor) queryParams.set("cursor", cursor);
  const response = await authRequest<
    Omit<AlbumDetails, "items"> & { items: CatalogItemWire[] }
  >(
    `/api/v1/albums/${albumId}?${queryParams}`,
    { signal },
    "Could not load this album",
  );
  return { ...response, items: response.items.map(mapItem) };
}

export function createAlbum(request: { name: string; description?: string }) {
  return authRequest<Album>(
    "/api/v1/albums",
    { method: "POST", body: JSON.stringify(request) },
    "Could not create the album",
  );
}

export function updateAlbum(
  albumId: string,
  request: { name: string; description?: string },
) {
  return authRequest<Album>(
    `/api/v1/albums/${albumId}`,
    { method: "PATCH", body: JSON.stringify(request) },
    "Could not update the album",
  );
}

export function deleteAlbum(albumId: string) {
  return authRequest<void>(
    `/api/v1/albums/${albumId}`,
    { method: "DELETE" },
    "Could not delete the album",
  );
}

export function setAlbumItems(
  albumId: string,
  itemIds: string[],
  included: boolean,
  signal?: AbortSignal,
) {
  return authRequest<BatchItemsResult>(
    `/api/v1/albums/${albumId}/items/batch`,
    {
      method: "POST",
      signal,
      body: JSON.stringify({ itemIds, included }),
    },
    included
      ? "Could not add the selected photos to the album"
      : "Could not remove the selected items from the album",
  );
}

export async function listAlbumShares(albumId: string): Promise<AlbumShare[]> {
  const response = await authRequest<{ shares: AlbumShare[] }>(
    `/api/v1/albums/${albumId}/shares`,
    undefined,
    "Could not load album access",
  );
  return response.shares;
}

export function shareAlbum(
  albumId: string,
  recipientUserId: string,
  role: AlbumShareRole = "viewer",
) {
  return authRequest<AlbumShare>(
    `/api/v1/albums/${albumId}/shares/${recipientUserId}`,
    { method: "PUT", body: JSON.stringify({ role }) },
    "Could not share the album",
  );
}

export function revokeAlbumShare(albumId: string, recipientUserId: string) {
  return authRequest<void>(
    `/api/v1/albums/${albumId}/shares/${recipientUserId}`,
    { method: "DELETE" },
    "Could not revoke album access",
  );
}

export function thumbnailUrl(id: string, size: "sm" | "md" | "lg" = "md") {
  return apiMediaUrl(`/api/v1/items/${id}/thumbnail?size=${size}`);
}

export async function listPhotos(options: {
  signal?: AbortSignal;
  cursor?: string;
  scope: PhotoScope;
  type: MediaTypeFilter;
  favorite?: boolean;
  folderId?: string;
  sort?: PhotoSort;
  year?: number;
  captureDate?: string;
  seekDate?: string;
  tagId?: string;
}): Promise<CatalogPage> {
  const query = new URLSearchParams({
    limit: "60",
    scope: options.scope,
    type: options.type,
  });
  if (options.cursor) query.set("cursor", options.cursor);
  if (options.favorite) query.set("favorite", "true");
  if (options.folderId) query.set("folderId", options.folderId);
  if (options.sort) query.set("sort", options.sort);
  if (options.year) query.set("year", String(options.year));
  if (options.captureDate) query.set("captureDate", options.captureDate);
  if (options.seekDate) query.set("seekDate", options.seekDate);
  if (options.tagId) query.set("tagId", options.tagId);
  return mapPage(
    await authRequest<CatalogPageWire>(
      `/api/v1/photos?${query}`,
      { signal: options.signal },
      "Could not load your Photos timeline",
    ),
  );
}

export async function listTimelineEvents(options?: {
  query?: string;
  limit?: number;
  signal?: AbortSignal;
}): Promise<TimelineEvent[]> {
  const query = new URLSearchParams({ limit: String(options?.limit ?? 500) });
  if (options?.query) query.set("q", options.query);
  const response = await authRequest<{ events: TimelineEvent[] }>(
    `/api/v1/timeline-events?${query}`,
    { signal: options?.signal },
    "Could not load timeline events",
  );
  return response.events;
}

export function createTimelineEvent(request: {
  title: string;
  startDate: string;
  endDate?: string;
  description?: string;
}) {
  return authRequest<TimelineEvent>(
    "/api/v1/timeline-events",
    { method: "POST", body: JSON.stringify(request) },
    "Could not create the timeline event",
  );
}

export function updateTimelineEvent(
  id: string,
  request: {
    title: string;
    startDate: string;
    endDate?: string;
    description?: string;
  },
) {
  return authRequest<TimelineEvent>(
    `/api/v1/timeline-events/${id}`,
    { method: "PATCH", body: JSON.stringify(request) },
    "Could not update the timeline event",
  );
}

export function deleteTimelineEvent(id: string) {
  return authRequest(
    `/api/v1/timeline-events/${id}`,
    { method: "DELETE" },
    "Could not delete the timeline event",
  );
}

export async function searchCatalog(options: {
  signal?: AbortSignal;
  query: string;
  scope: "shared" | "photos" | "trash";
  photoScope?: PhotoScope;
  type?: FileTypeFilter;
  cursor?: string;
  tagId?: string;
}): Promise<CatalogPage> {
  const query = new URLSearchParams({
    limit: "60",
    q: options.query,
    scope: options.scope,
  });
  if (options.photoScope) query.set("photoScope", options.photoScope);
  if (options.type) query.set("type", options.type);
  if (options.cursor) query.set("cursor", options.cursor);
  if (options.tagId) query.set("tagId", options.tagId);
  return mapPage(
    await authRequest<CatalogPageWire>(
      `/api/v1/search?${query}`,
      { signal: options.signal },
      "Could not search your catalog",
    ),
  );
}

export async function listPhotoYears(options: {
  scope: PhotoScope;
  type: MediaTypeFilter;
  favorite?: boolean;
  tagId?: string;
}): Promise<number[]> {
  const query = new URLSearchParams({
    scope: options.scope,
    type: options.type,
  });
  if (options.favorite) query.set("favorite", "true");
  if (options.tagId) query.set("tagId", options.tagId);
  const response = await authRequest<{ years: number[] }>(
    `/api/v1/photos/years?${query}`,
    undefined,
    "Could not load timeline years",
  );
  return response.years;
}

export async function listPhotoTimelineAnchors(options: {
  signal?: AbortSignal;
  scope: PhotoScope;
  type: MediaTypeFilter;
  favorite?: boolean;
  folderId?: string;
  sort: "captured_desc" | "captured_asc";
  year?: number;
  captureDate?: string;
  tagId?: string;
}): Promise<PhotoTimelineAnchors> {
  const query = new URLSearchParams({
    scope: options.scope,
    type: options.type,
    sort: options.sort,
  });
  if (options.favorite) query.set("favorite", "true");
  if (options.folderId) query.set("folderId", options.folderId);
  if (options.year) query.set("year", String(options.year));
  if (options.captureDate) query.set("captureDate", options.captureDate);
  if (options.tagId) query.set("tagId", options.tagId);
  return authRequest<PhotoTimelineAnchors>(
    `/api/v1/photos/timeline-anchors?${query}`,
    { signal: options.signal },
    "Could not load timeline navigation",
  );
}

export async function getItem(id: string) {
  return mapItem(
    await authRequest<CatalogItemWire>(
      `/api/v1/items/${id}`,
      undefined,
      "Could not load item details",
    ),
  );
}

export async function createFolder(name: string, parentId?: string) {
  return mapItem(
    await authRequest<CatalogItemWire>(
      "/api/v1/items/folders",
      {
        method: "POST",
        body: JSON.stringify({ name, parentId: parentId || null }),
      },
      "Could not create the folder",
    ),
  );
}

export async function renameItem(id: string, name: string) {
  return mapItem(
    await authRequest<CatalogItemWire>(
      `/api/v1/items/${id}`,
      { method: "PATCH", body: JSON.stringify({ name }) },
      "Could not rename this item",
    ),
  );
}

export async function moveItem(id: string, parentId?: string) {
  return mapItem(
    await authRequest<CatalogItemWire>(
      `/api/v1/items/${id}/move`,
      { method: "POST", body: JSON.stringify({ parentId: parentId || null }) },
      "Could not move this item",
    ),
  );
}

export async function copyFile(id: string, parentId?: string) {
  return mapItem(
    await authRequest<CatalogItemWire>(
      `/api/v1/items/${id}/copy`,
      {
        method: "POST",
        body: JSON.stringify({ parentId: parentId || null }),
      },
      "Could not make a copy of this file",
    ),
  );
}

export async function setFavorite(id: string, favorite: boolean) {
  return mapItem(
    await authRequest<CatalogItemWire>(
      `/api/v1/items/${id}/favorite`,
      { method: "POST", body: JSON.stringify({ favorite }) },
      "Could not update favorite",
    ),
  );
}

export async function retryMetadata(id: string) {
  return mapItem(
    await authRequest<CatalogItemWire>(
      `/api/v1/items/${id}/metadata/retry`,
      { method: "POST" },
      "Could not retry metadata extraction",
    ),
  );
}

export function setCaptureDate(
  itemIds: string[],
  request: { capturedOn?: string; reset?: boolean },
) {
  return authRequest<BatchItemsResult>(
    "/api/v1/items/metadata/capture-date/batch",
    {
      method: "POST",
      body: JSON.stringify({ itemIds, ...request }),
    },
    "Could not update capture dates",
  );
}

export async function trashItem(id: string) {
  return mapItem(
    await authRequest<CatalogItemWire>(
      `/api/v1/items/${id}/trash`,
      { method: "POST" },
      "Could not move this item to trash",
    ),
  );
}

export async function restoreItem(id: string) {
  return mapItem(
    await authRequest<CatalogItemWire>(
      `/api/v1/items/${id}/restore`,
      { method: "POST" },
      "Could not restore this item",
    ),
  );
}

export function batchItems(request: {
  action: "favorite" | "move" | "trash" | "restore";
  itemIds: string[];
  favorite?: boolean;
  parentId?: string;
}) {
  return authRequest<BatchItemsResult>(
    "/api/v1/items/batch",
    {
      method: "POST",
      body: JSON.stringify({
        ...request,
        parentId: request.parentId || null,
      }),
    },
    "Could not update the selected items",
  );
}

export function listTags() {
  return authRequest<CatalogTag[]>(
    "/api/v1/tags",
    undefined,
    "Could not load tags",
  );
}

export function createTag(request: {
  name: string;
  color: CatalogTag["color"];
}) {
  return authRequest<CatalogTag>(
    "/api/v1/tags",
    { method: "POST", body: JSON.stringify(request) },
    "Could not create the tag",
  );
}

export function updateTag(
  tagId: string,
  request: { name: string; color: CatalogTag["color"] },
) {
  return authRequest<CatalogTag>(
    `/api/v1/tags/${tagId}`,
    { method: "PATCH", body: JSON.stringify(request) },
    "Could not update the tag",
  );
}

export function deleteTag(tagId: string) {
  return authRequest<void>(
    `/api/v1/tags/${tagId}`,
    { method: "DELETE" },
    "Could not delete the tag",
  );
}

export function setTagItems(
  tagId: string,
  itemIds: string[],
  included: boolean,
) {
  return authRequest<BatchItemsResult>(
    `/api/v1/tags/${tagId}/items/batch`,
    { method: "POST", body: JSON.stringify({ itemIds, included }) },
    "Could not update tag assignments",
  );
}

export async function getTrashSummary() {
  return authRequest<TrashSummary>(
    "/api/v1/items/trash/summary",
    undefined,
    "Could not load the current Trash count",
  );
}

export async function previewTrash(itemIds: string[]) {
  return authRequest<TrashPreview>(
    "/api/v1/items/trash/preview",
    { method: "POST", body: JSON.stringify({ itemIds }) },
    "Could not preview this Trash operation",
  );
}

export async function getStorageSummary(options?: { refresh?: boolean }) {
  const query = options?.refresh ? "?refresh=true" : "";
  return authRequest<StorageSummary>(
    `/api/v1/items/storage-summary${query}`,
    undefined,
    "Could not load storage usage",
  );
}

export async function clearTrash(options: {
  confirmationPhrase: string;
  expectedFileCount: number;
  expectedFolderCount: number;
  expectedOriginalCount: number;
  expectedOriginalBytes: number;
}) {
  return authRequest<ClearTrashResult>(
    "/api/v1/items/trash/clear",
    {
      method: "POST",
      body: JSON.stringify({
        confirmed: true,
        confirmationPhrase: options.confirmationPhrase,
        expectedFileCount: options.expectedFileCount,
        expectedFolderCount: options.expectedFolderCount,
        expectedOriginalCount: options.expectedOriginalCount,
        expectedOriginalBytes: options.expectedOriginalBytes,
      }),
    },
    "Could not clear trash",
  );
}

export async function runDeveloperStorageAudit() {
  return authRequest<DeveloperMaintenanceReport>(
    "/api/v1/developer/maintenance/audit",
    { method: "POST" },
    "Could not run the storage audit",
  );
}

export async function getDeveloperBackupStatus() {
  return authRequest<DeveloperBackupStatus>(
    "/api/v1/developer/maintenance/backup-status",
    undefined,
    "Could not load backup status",
  );
}

export async function getDeveloperOperationalHealth() {
  return authRequest<DeveloperOperationalHealth>(
    "/api/v1/developer/maintenance/health",
    undefined,
    "Could not load operational health",
  );
}

export async function getClusterHosts() {
  return authRequest<ClusterHosts>(
    "/api/v1/developer/maintenance/hosts",
    undefined,
    "Could not load the PhotoDrive servers",
  );
}

export async function setPrimaryHost(hostId: string) {
  return authRequest<ClusterHosts>(
    "/api/v1/developer/maintenance/primary-host",
    { method: "PUT", body: JSON.stringify({ hostId }) },
    "Could not change the primary server",
  );
}

export async function runDeveloperBackup() {
  return authRequest<DeveloperBackupReport>(
    "/api/v1/developer/maintenance/backup",
    { method: "POST" },
    "Could not run the verified backup",
  );
}

export async function cleanupStorageInconsistencies(options: {
  candidateIds: string[];
  expectedCandidateCount: number;
  expectedCandidateBytes: number;
  confirmationPhrase: string;
}) {
  return authRequest<CleanupStorageInconsistenciesResult>(
    "/api/v1/developer/maintenance/cleanup",
    {
      method: "POST",
      body: JSON.stringify({
        confirmed: true,
        candidateIds: options.candidateIds,
        expectedCandidateCount: options.expectedCandidateCount,
        expectedCandidateBytes: options.expectedCandidateBytes,
        confirmationPhrase: options.confirmationPhrase,
      }),
    },
    "Could not clean the selected storage inconsistencies",
  );
}

export async function getCacheBuildOptions() {
  return authRequest<CacheBuildOptions>(
    "/api/v1/system/cache-builds/options",
    undefined,
    "Could not load cache-build options",
  );
}

export async function previewCacheBuild(request: CacheBuildRequest) {
  return authRequest<CacheBuildPreview>(
    "/api/v1/system/cache-builds/preview",
    { method: "POST", body: JSON.stringify(request) },
    "Could not preview the cache build",
  );
}

export async function startCacheBuild(
  request: CacheBuildRequest,
  preview: CacheBuildPreview,
) {
  return authRequest<CacheBuildJob>(
    "/api/v1/system/cache-builds",
    {
      method: "POST",
      body: JSON.stringify({
        ...request,
        expectedSelectedItemCount: preview.selectedItemCount,
        expectedQueuedItemCount: preview.queuedItemCount,
        expectedDownloadBytes: preview.downloadBytes,
      }),
    },
    "Could not start the cache build",
  );
}

export async function listCacheBuilds() {
  return authRequest<CacheBuildJob[]>(
    "/api/v1/system/cache-builds",
    undefined,
    "Could not load cache-build jobs",
  );
}

export async function cancelCacheBuild(id: string) {
  return authRequest<CacheBuildJob>(
    `/api/v1/system/cache-builds/${id}/cancel`,
    { method: "POST" },
    "Could not cancel the cache build",
  );
}

export async function retryCacheBuild(id: string) {
  return authRequest<CacheBuildJob>(
    `/api/v1/system/cache-builds/${id}/retry`,
    { method: "POST" },
    "Could not retry failed cache items",
  );
}

export async function createUploadSession(
  file: Pick<File, "name" | "type" | "size">,
  options: UploadDestination,
  signal?: AbortSignal,
  clientUploadId?: string,
) {
  return authRequest<UploadSessionResponse>(
    "/api/v1/uploads",
    {
      method: "POST",
      signal,
      body: JSON.stringify({
        clientUploadId: clientUploadId || null,
        name: file.name,
        mimeType: file.type || "application/octet-stream",
        sizeBytes: file.size,
        parentId: options.parentId || null,
        useDefaultMediaFolder: Boolean(options.useDefaultMediaFolder),
        useDefaultDocumentsFolder: Boolean(options.useDefaultDocumentsFolder),
        allowNameConflict: Boolean(options.allowNameConflict),
      }),
    },
    "Could not start the upload",
  );
}

export async function getUploadSession(id: string, signal?: AbortSignal) {
  return authRequest<Required<UploadSessionResponse>>(
    `/api/v1/uploads/${id}`,
    { signal },
    "Could not restore the upload session",
  );
}

export async function appendUploadChunk(
  id: string,
  chunk: Blob,
  offset: number,
  total: number,
  signal?: AbortSignal,
) {
  return authRequest<UploadProgressResponse>(
    `/api/v1/uploads/${id}`,
    {
      method: "PATCH",
      signal,
      headers: {
        "Content-Type": "application/octet-stream",
        "Content-Range": `bytes ${offset}-${offset + chunk.size - 1}/${total}`,
      },
      body: chunk,
    },
    "Could not upload this file chunk",
  );
}

export async function completeUploadSession(
  id: string,
  sha256: string,
  allowNameConflict: boolean,
  signal?: AbortSignal,
) {
  const response = await authRequest<CompleteUploadResponse>(
    `/api/v1/uploads/${id}/complete`,
    {
      method: "POST",
      signal,
      body: JSON.stringify({ sha256, allowNameConflict }),
    },
    "Could not finish the upload",
  );
  return {
    item: mapItem(response.item),
    duplicateState: response.duplicateState,
  };
}

export async function cancelUploadSession(id: string, signal?: AbortSignal) {
  await authRequest(
    `/api/v1/uploads/${id}`,
    { method: "DELETE", signal },
    "Could not cancel the upload",
  );
}

export function contentUrl(id: string) {
  return apiMediaUrl(`/api/v1/items/${id}/content`);
}

export async function getTextContent(id: string, signal?: AbortSignal) {
  const response = await fetch(contentUrl(id), {
    cache: "no-store",
    credentials: "include",
    signal,
  });
  if (!response.ok) throw new Error("Could not load the document preview");
  return response.text();
}

export function downloadItemsUrl(ids: string[]) {
  const query = new URLSearchParams();
  ids.forEach((id) => query.append("ids", id));
  return apiMediaUrl(`/api/v1/items/download?${query}`);
}

export type DownloadPreview = {
  fileCount: number;
  totalBytes: number;
};

export function previewItemsDownload(ids: string[], signal?: AbortSignal) {
  const query = new URLSearchParams();
  ids.forEach((id) => query.append("ids", id));
  return authRequest<DownloadPreview>(
    `/api/v1/items/download/preview?${query}`,
    { signal },
    "Could not calculate the download size",
  );
}

export function previewFolderDownload(folderId: string, signal?: AbortSignal) {
  return authRequest<DownloadPreview>(
    `/api/v1/items/folders/${folderId}/download/preview`,
    { signal },
    "Could not calculate the folder download size",
  );
}

export function previewAlbumDownload(albumId: string, signal?: AbortSignal) {
  return authRequest<DownloadPreview>(
    `/api/v1/albums/${albumId}/download/preview`,
    { signal },
    "Could not calculate the album download size",
  );
}

export function downloadFolderUrl(folderId: string) {
  return apiMediaUrl(`/api/v1/items/folders/${folderId}/download`);
}

export function downloadAlbumUrl(albumId: string) {
  return apiMediaUrl(`/api/v1/albums/${albumId}/download`);
}

function mapPage(page: CatalogPageWire): CatalogPage {
  return {
    items: page.items.map(mapItem),
    nextCursor: page.nextCursor,
    previousCursor: page.previousCursor,
  };
}

function mapItem(item: CatalogItemWire): MediaItem {
  return {
    ...item,
    favorite: Boolean(item.favorite),
    capabilities: item.capabilities ?? {
      canRename: true,
      canMove: true,
      canTrash: true,
      canFavorite: true,
      canRetryMetadata: true,
      canShare: true,
    },
    accessRole: item.accessRole ?? "owner",
    thumbnailUrl: item.thumbnailPath
      ? apiMediaUrl(item.thumbnailPath)
      : undefined,
  };
}

function apiMediaUrl(path: string) {
  return apiUrl(path);
}

export const isDemoMode = !apiEnabled;
