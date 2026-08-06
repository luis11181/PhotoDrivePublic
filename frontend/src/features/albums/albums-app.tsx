"use client";

/* eslint-disable @next/next/no-img-element -- album covers are authenticated backend thumbnails. */

import {
  useInfiniteQuery,
  useQuery,
  useQueryClient,
} from "@tanstack/react-query";
import {
  Album as AlbumIcon,
  ArrowLeft,
  Download,
  Heart,
  ImageOff,
  ImageMinus,
  ImagePlus,
  Pencil,
  Plus,
  RotateCcw,
  Share2,
  Trash2,
  X,
} from "lucide-react";
import { usePathname, useRouter, useSearchParams } from "next/navigation";
import Link from "next/link";
import { useEffect, useMemo, useRef, useState } from "react";
import type { CurrentUser } from "@/lib/auth";
import {
  deleteAlbum,
  downloadAlbumUrl,
  downloadItemsUrl,
  getAlbum,
  getItem,
  getStorageSummary,
  listTags,
  listAlbums,
  renameItem,
  retryMetadata,
  setAlbumItems,
  setFavorite,
  thumbnailUrl,
  trashItem,
} from "@/lib/api";
import type {
  Album,
  AlbumItemFilters,
  CatalogTag,
  MediaItem,
  MediaTypeFilter,
  PhotoSort,
} from "@/lib/types";
import { usePreferences } from "@/lib/preferences";
import { ItemDetails } from "@/features/library/components/item-details";
import { LibrarySidebar } from "@/features/library/components/library-sidebar";
import { LibraryTopbar } from "@/features/library/components/library-topbar";
import { MediaCard } from "@/features/library/components/media-card";
import { CatalogItemContextMenu } from "@/features/catalog/components/catalog-item-context-menu";
import { CatalogKeyboardShortcutsDialog } from "@/features/catalog/components/catalog-keyboard-shortcuts-dialog";
import { useCatalogSelection } from "@/features/catalog/use-catalog-selection";
import { useCatalogContextMenu } from "@/features/catalog/use-catalog-context-menu";
import { useCatalogKeyboardShortcuts } from "@/features/catalog/use-catalog-keyboard-shortcuts";
import { PhotoViewer } from "@/features/library/components/photo-viewer";
import { SelectionActions } from "@/features/library/components/selection-actions";
import { ShareDialog } from "@/features/library/components/share-dialog";
import { TrashPreviewDialog } from "@/features/library/components/trash-preview-dialog";
import { RenameItemDialog } from "@/features/library/components/rename-item-dialog";
import { DownloadConfirmationDialog } from "@/features/library/components/download-confirmation-dialog";
import { catalogPreviewHref } from "@/features/catalog/catalog-item-links";
import {
  AddPhotosToAlbumDialog,
  AddToAlbumDialog,
  AlbumEditorDialog,
  AlbumShareDialog,
} from "./album-dialogs";

type AlbumsAppProps = {
  user: CurrentUser;
  onOpenAccount: () => void;
  onSignOut: () => Promise<void>;
};

export function AlbumsApp(props: AlbumsAppProps) {
  const albumId = useSearchParams().get("album") ?? undefined;
  return (
    <AlbumsWorkspace
      key={albumId ?? "album-list"}
      {...props}
      albumId={albumId}
    />
  );
}

function AlbumsWorkspace({
  user,
  onOpenAccount,
  onSignOut,
  albumId,
}: AlbumsAppProps & { albumId?: string }) {
  const { t } = usePreferences();
  const router = useRouter();
  const pathname = usePathname();
  const searchParams = useSearchParams();
  const queryClient = useQueryClient();
  const viewerItemId = searchParams.get("preview");
  const [query, setQuery] = useState("");
  const [mediaType, setMediaType] = useState<MediaTypeFilter>("all");
  const [photoSort, setPhotoSort] = useState<PhotoSort>("added_desc");
  const [photoYear, setPhotoYear] = useState<number | undefined>();
  const [selectedTagId, setSelectedTagId] = useState("");
  const [favoritesOnly, setFavoritesOnly] = useState(false);
  const [mobileNavigation, setMobileNavigation] = useState(false);
  const [editing, setEditing] = useState<Album | "new" | null>(null);
  const [sharing, setSharing] = useState<Album | null>(null);
  const [viewerInfoOpen, setViewerInfoOpen] = useState(false);
  const previewOpenedHere = useRef(false);
  const [renamingItem, setRenamingItem] = useState<MediaItem | null>(null);
  const [sharingItems, setSharingItems] = useState<MediaItem[]>([]);
  const [addingItems, setAddingItems] = useState<MediaItem[]>([]);
  const [addingPhotos, setAddingPhotos] = useState(false);
  const [pendingTrash, setPendingTrash] = useState<MediaItem[]>([]);
  const [pendingAlbumDownload, setPendingAlbumDownload] =
    useState<Album | null>(null);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState("");

  const albums = useQuery({
    queryKey: ["albums", "all"],
    queryFn: () => listAlbums("all"),
  });
  const storage = useQuery({
    queryKey: ["storage-summary"],
    queryFn: () => getStorageSummary(),
    staleTime: Infinity,
  });
  const normalizedAlbumQuery = albumId ? query.trim() : "";
  const albumFilters: AlbumItemFilters = {
    query: normalizedAlbumQuery || undefined,
    type: mediaType,
    favorite: favoritesOnly,
    sort: photoSort,
    year: photoYear,
    tagId: selectedTagId || undefined,
  };
  const detail = useInfiniteQuery({
    queryKey: ["albums", "detail", albumId, albumFilters],
    queryFn: ({ pageParam, signal }) =>
      getAlbum(albumId!, albumFilters, pageParam, signal),
    initialPageParam: undefined as string | undefined,
    getNextPageParam: (page) => page.nextCursor,
    enabled: Boolean(albumId),
  });
  const album =
    detail.data?.pages[0]?.album ??
    albums.data?.find((candidate) => candidate.id === albumId);
  const tags = useQuery({
    queryKey: ["tags"],
    queryFn: listTags,
    enabled: Boolean(albumId && album?.accessRole === "owner"),
    staleTime: 30_000,
  });
  const items = useMemo(
    () => detail.data?.pages.flatMap((page) => page.items) ?? [],
    [detail.data],
  );
  const availableYears = detail.data?.pages[0]?.availableYears ?? [];
  const hasActiveAlbumFilters = Boolean(
    normalizedAlbumQuery ||
      mediaType !== "all" ||
      photoSort !== "added_desc" ||
      photoYear !== undefined ||
      selectedTagId ||
      favoritesOnly,
  );
  const {
    selectedIds,
    selectedItems,
    allVisibleSelected,
    clearSelection,
    selectItem,
    selectItemById,
    toggleAllVisibleItems: toggleAllItems,
  } = useCatalogSelection({ items });
  const { contextMenu, setContextMenu, openContextMenu } =
    useCatalogContextMenu();
  const { shortcutsOpen, setShortcutsOpen, handleCatalogKeyDown } =
    useCatalogKeyboardShortcuts({
      disabled: Boolean(!albumId || viewerItemId || pendingTrash.length > 0),
      selectedItems,
      onSelectAll: toggleAllItems,
      onClearSelection: clearSelection,
      onOpen: openViewer,
      onRename: setRenamingItem,
      onTrash: (item) => setPendingTrash([item]),
      onDetails: (item) => {
        openViewer(item);
        setViewerInfoOpen(true);
      },
      onEscape: () => setContextMenu(null),
    });
  const viewerIndex = items.findIndex((item) => item.id === viewerItemId);
  const catalogViewerItem = viewerIndex >= 0 ? items[viewerIndex] : undefined;
  const routedViewerQuery = useQuery({
    queryKey: ["catalog-item", viewerItemId],
    queryFn: () => getItem(viewerItemId!),
    enabled: Boolean(viewerItemId) && !catalogViewerItem,
    retry: false,
  });
  const routedViewerItem = catalogViewerItem ?? routedViewerQuery.data;
  const viewerItem =
    routedViewerItem?.kind === "image" || routedViewerItem?.kind === "video"
      ? routedViewerItem
      : null;
  const filteredAlbums = useMemo(() => {
    const normalized = query.trim().toLowerCase();
    return (
      albums.data?.filter(
        (candidate) =>
          !normalized ||
          candidate.name.toLowerCase().includes(normalized) ||
          candidate.description?.toLowerCase().includes(normalized),
      ) ?? []
    );
  }, [albums.data, query]);

  useEffect(() => {
    if (viewerItemId) return;
    previewOpenedHere.current = false;
  }, [viewerItemId]);

  function openViewer(item: MediaItem) {
    setViewerInfoOpen(false);
    if (viewerItemId) {
      router.replace(
        catalogPreviewHref(pathname, searchParams.toString(), item.id),
        { scroll: false },
      );
      return;
    }
    previewOpenedHere.current = true;
    router.push(
      catalogPreviewHref(pathname, searchParams.toString(), item.id),
      {
        scroll: false,
      },
    );
  }

  function navigateViewer(itemId: string) {
    router.replace(
      catalogPreviewHref(pathname, searchParams.toString(), itemId),
      { scroll: false },
    );
  }

  function dismissViewerOverlay() {
    setViewerInfoOpen(false);
    if (!viewerItemId) return;
    if (previewOpenedHere.current) {
      previewOpenedHere.current = false;
      router.back();
      return;
    }
    router.replace(catalogPreviewHref(pathname, searchParams.toString()), {
      scroll: false,
    });
  }

  async function refreshStorage() {
    try {
      await queryClient.fetchQuery({
        queryKey: ["storage-summary"],
        queryFn: () => getStorageSummary({ refresh: true }),
        staleTime: 0,
      });
    } catch (reason) {
      setMessage(
        reason instanceof Error
          ? reason.message
          : t("sidebar.storageUnavailable"),
      );
    }
  }

  function download(itemsToDownload: MediaItem[]) {
    const downloadable = itemsToDownload.filter(
      (item) => item.kind !== "folder" && !item.trashedAt,
    );
    if (!downloadable.length) {
      setMessage(t("albums.downloadNone"));
      return;
    }
    if (downloadable.length > 100) {
      setMessage(t("albums.downloadLimit"));
      return;
    }
    const link = document.createElement("a");
    link.href = downloadItemsUrl(downloadable.map((item) => item.id));
    link.download = downloadable.length === 1 ? downloadable[0].name : "";
    link.rel = "noopener";
    document.body.appendChild(link);
    link.click();
    link.remove();
    setMessage(
      downloadable.length === 1
        ? t("albums.downloading", { name: downloadable[0].name })
        : t("albums.preparingZip", { count: downloadable.length }),
    );
  }

  function downloadAlbum(albumToDownload: Album) {
    setPendingAlbumDownload(albumToDownload);
  }

  function confirmAlbumDownload() {
    if (!pendingAlbumDownload) return;
    const link = document.createElement("a");
    link.href = downloadAlbumUrl(pendingAlbumDownload.id);
    link.download = `${pendingAlbumDownload.name}.zip`;
    link.rel = "noopener";
    document.body.appendChild(link);
    link.click();
    link.remove();
    setMessage(
      t("albums.preparingAlbumZip", { name: pendingAlbumDownload.name }),
    );
    setPendingAlbumDownload(null);
  }

  async function mutateItems({
    source,
    eligible,
    action,
    successLabel,
    clearAfter = true,
  }: {
    source: MediaItem[];
    eligible: (item: MediaItem) => boolean;
    action: (item: MediaItem) => Promise<unknown>;
    successLabel: string;
    clearAfter?: boolean;
  }) {
    const supported = source.filter(eligible);
    if (!supported.length) {
      setMessage(t("albums.unsupported"));
      return;
    }

    setBusy(true);
    setMessage("");
    let succeeded = 0;
    try {
      for (const item of supported) {
        await action(item);
        succeeded += 1;
      }
      await Promise.all([
        detail.refetch(),
        queryClient.invalidateQueries({ queryKey: ["albums"] }),
        queryClient.invalidateQueries({ queryKey: ["library"] }),
      ]);
      if (clearAfter) clearSelection();
      setMessage(
        t("albums.result", {
          count: succeeded,
          items: t(succeeded === 1 ? "albums.item" : "albums.items"),
          result: successLabel,
        }),
      );
    } catch (reason) {
      setMessage(
        t("albums.completed", {
          count: succeeded,
          error:
            reason instanceof Error
              ? reason.message
              : t("albums.operationFailed"),
        }),
      );
    } finally {
      setBusy(false);
    }
  }

  async function remove(itemsToRemove: MediaItem[]) {
    if (!album) return;
    if (album.accessRole !== "owner" || !itemsToRemove.length) return;
    setBusy(true);
    setMessage("");
    try {
      const result = await setAlbumItems(
        album.id,
        itemsToRemove.map((item) => item.id),
        false,
      );
      await Promise.all([
        detail.refetch(),
        queryClient.invalidateQueries({ queryKey: ["albums"] }),
      ]);
      clearSelection();
      setMessage(
        t("albums.batchRemoved", {
          count: result.succeededCount,
          unchanged: result.unchangedCount,
        }),
      );
    } catch (reason) {
      setMessage(
        reason instanceof Error ? reason.message : t("albums.operationFailed"),
      );
    } finally {
      setBusy(false);
    }
  }

  async function removeAlbum() {
    if (
      !album ||
      !window.confirm(t("albums.deleteConfirm", { name: album.name }))
    )
      return;
    setBusy(true);
    try {
      await deleteAlbum(album.id);
      await queryClient.invalidateQueries({ queryKey: ["albums"] });
      router.push("/albums");
    } catch (reason) {
      setMessage(
        reason instanceof Error ? reason.message : t("albums.deleteFailed"),
      );
    } finally {
      setBusy(false);
    }
  }

  async function favorite(item: MediaItem) {
    setBusy(true);
    try {
      await setFavorite(item.id, !item.favorite);
      await Promise.all([
        detail.refetch(),
        queryClient.invalidateQueries({
          queryKey: ["library", "favorites"],
        }),
      ]);
    } catch (reason) {
      setMessage(
        reason instanceof Error ? reason.message : t("albums.favoriteFailed"),
      );
    } finally {
      setBusy(false);
    }
  }

  async function renameViewerItem(item: MediaItem, name: string) {
    setBusy(true);
    try {
      await renameItem(item.id, name);
      await Promise.all([
        detail.refetch(),
        queryClient.invalidateQueries({ queryKey: ["library"] }),
      ]);
      return true;
    } catch (reason) {
      setMessage(
        reason instanceof Error ? reason.message : t("albums.operationFailed"),
      );
      return false;
    } finally {
      setBusy(false);
    }
  }

  return (
    <main className="app-shell" onKeyDown={handleCatalogKeyDown}>
      <LibrarySidebar
        view="albums"
        open={mobileNavigation}
        onClose={() => setMobileNavigation(false)}
        storageSummary={storage.data}
        storageLoading={storage.isLoading}
        storageRefreshing={storage.isFetching}
        onRefreshStorage={() => void refreshStorage()}
        isOwner={user.isOwner}
      />
      <section className="workspace">
        <LibraryTopbar
          query={query}
          onQueryChange={setQuery}
          placeholder={t(albumId ? "albums.searchItems" : "albums.filter")}
          onOpenNavigation={() => setMobileNavigation(true)}
          onOpenAccount={onOpenAccount}
          onSignOut={onSignOut}
          onToggleNotifications={() => undefined}
          notificationCount={0}
          notificationsOpen={false}
          operationRunning={busy}
          user={user}
        />
        <div className="content">
          {albumId && (
            <button
              className="album-back-link"
              onClick={() => router.push("/albums")}
            >
              <ArrowLeft /> {t("albums.breadcrumb")}
            </button>
          )}
          <div className={`title-row${albumId ? " album-title-row" : ""}`}>
            <div>
              <p className="eyebrow">
                {albumId ? t("albums.photoAlbum") : t("library.photos")}
              </p>
              <h1>{album?.name ?? t("albums.title")}</h1>
              {album?.description ? (
                <p className="album-description">{album.description}</p>
              ) : (
                !albumId && (
                  <p className="album-description">{t("albums.intro")}</p>
                )
              )}
            </div>
            <div className="primary-actions">
              {!albumId && (
                <button className="primary" onClick={() => setEditing("new")}>
                  <Plus size={17} /> {t("albums.new")}
                </button>
              )}
              {album && (
                <button
                  className="secondary"
                  onClick={() => downloadAlbum(album)}
                >
                  <Download size={16} /> {t("albums.downloadZip")}
                </button>
              )}
              {album?.accessRole === "owner" && (
                <>
                  <button
                    className="primary"
                    onClick={() => setAddingPhotos(true)}
                  >
                    <ImagePlus size={17} /> {t("albums.addPhotos")}
                  </button>
                  <button
                    className="secondary"
                    onClick={() => setEditing(album)}
                  >
                    <Pencil size={16} /> {t("albums.edit")}
                  </button>
                  <button
                    className="secondary"
                    onClick={() => setSharing(album)}
                  >
                    <Share2 size={16} /> {t("albums.share")}
                  </button>
                  <button
                    className="secondary destructive"
                    disabled={busy}
                    onClick={() => void removeAlbum()}
                  >
                    <Trash2 size={16} /> {t("albums.delete")}
                  </button>
                </>
              )}
            </div>
          </div>
          {album?.accessRole === "viewer" && (
            <p className="status-note">
              {album.sharedByDisplayName
                ? t("albums.sharedNamed", {
                    name: album.sharedByDisplayName,
                  })
                : t("albums.shared")}
            </p>
          )}
          {message && <p className="status-note">{message}</p>}

          {albumId && (
            <AlbumFilterBar
              mediaType={mediaType}
              onMediaTypeChange={setMediaType}
              photoSort={photoSort}
              onPhotoSortChange={setPhotoSort}
              years={availableYears}
              year={photoYear}
              onYearChange={setPhotoYear}
              tags={album?.accessRole === "owner" ? (tags.data ?? []) : []}
              selectedTagId={selectedTagId}
              onSelectedTagChange={setSelectedTagId}
              favoritesOnly={favoritesOnly}
              onToggleFavorites={() => setFavoritesOnly((current) => !current)}
              hasActiveFilters={hasActiveAlbumFilters}
              onReset={() => {
                setQuery("");
                setMediaType("all");
                setPhotoSort("added_desc");
                setPhotoYear(undefined);
                setSelectedTagId("");
                setFavoritesOnly(false);
              }}
            />
          )}

          {!albumId ? (
            <section className="library-section" aria-live="polite">
              <div className="section-heading">
                <div>
                  <h2>{t("albums.collections")}</h2>
                  <span className="item-count">
                    {t("albums.count", { count: filteredAlbums.length })}
                  </span>
                </div>
              </div>
              {albums.isPending ? (
                <AlbumEmpty title={t("albums.loading")} />
              ) : albums.isError ? (
                <AlbumEmpty title={albums.error.message} />
              ) : filteredAlbums.length ? (
                <div className="album-grid">
                  {filteredAlbums.map((candidate) => (
                    <Link
                      className="album-card"
                      key={candidate.id}
                      href={`/albums?album=${encodeURIComponent(candidate.id)}`}
                    >
                      <AlbumCover album={candidate} />
                      <span className="album-card-copy">
                        <strong>{candidate.name}</strong>
                        <small>
                          {candidate.memberCount}{" "}
                          {candidate.memberCount === 1
                            ? t("albums.item")
                            : t("albums.items")}
                          {candidate.accessRole === "viewer"
                            ? ` · ${t("albums.sharedBy", {
                                name:
                                  candidate.sharedByDisplayName ??
                                  t("albums.family"),
                              })}`
                            : ""}
                        </small>
                      </span>
                    </Link>
                  ))}
                </div>
              ) : (
                <AlbumEmpty
                  title={query ? t("albums.noMatches") : t("albums.emptyHint")}
                />
              )}
            </section>
          ) : (
            <section className="library-section" aria-live="polite">
              <div className="section-heading">
                <div>
                  <h2>{t("albums.albumItems")}</h2>
                  <span className="item-count">
                    {t("albums.loaded", { count: items.length })}
                  </span>
                </div>
              </div>
              {selectedItems.length > 0 && (
                <SelectionActions
                  selectedItems={selectedItems}
                  folderOptions={[]}
                  view="albums"
                  busy={busy}
                  onOpen={(item) => {
                    openViewer(item);
                    clearSelection();
                  }}
                  onDownload={download}
                  onFavorite={(favoriteValue) =>
                    void mutateItems({
                      source: selectedItems,
                      eligible: (item) =>
                        Boolean(item.capabilities.canFavorite) &&
                        !item.trashedAt,
                      action: (item) => setFavorite(item.id, favoriteValue),
                      successLabel: favoriteValue
                        ? t("albums.addedFavorites")
                        : t("albums.removedFavorites"),
                    })
                  }
                  onMove={() => undefined}
                  onTrash={() =>
                    setPendingTrash(
                      selectedItems.filter(
                        (item) => item.capabilities.canTrash && !item.trashedAt,
                      ),
                    )
                  }
                  onRestore={() => undefined}
                  onRetryMetadata={() =>
                    void mutateItems({
                      source: selectedItems,
                      eligible: (item) =>
                        Boolean(item.capabilities.canRetryMetadata) &&
                        item.metadata?.state === "failed" &&
                        !item.trashedAt,
                      action: (item) => retryMetadata(item.id),
                      successLabel: t("albums.queuedMetadata"),
                    })
                  }
                  onShare={setSharingItems}
                  onAddToAlbum={setAddingItems}
                  onRemoveFromAlbum={
                    album?.accessRole === "owner"
                      ? () => void remove(selectedItems)
                      : undefined
                  }
                  allVisibleSelected={allVisibleSelected}
                  onSelectAll={toggleAllItems}
                  onClear={clearSelection}
                />
              )}
              {detail.isPending ? (
                <AlbumEmpty title={t("albums.loadingAlbum")} />
              ) : detail.isError ? (
                <AlbumEmpty title={detail.error.message} />
              ) : items.length ? (
                <>
                  <div className="media-grid">
                    {items.map((item) => (
                      <div className="album-item" key={item.id}>
                        <MediaCard
                          item={item}
                          selected={selectedIds.includes(item.id)}
                          selectionMode={selectedIds.length > 0}
                          onSelect={(range) => selectItem(item, Boolean(range))}
                          onKeyboardRange={selectItemById}
                          onSelectAll={toggleAllItems}
                          onOpen={() => openViewer(item)}
                          onContextMenu={(event) =>
                            openContextMenu(event, item)
                          }
                          photo
                        />
                      </div>
                    ))}
                  </div>
                  {detail.hasNextPage && (
                    <button
                      className="secondary load-more"
                      disabled={detail.isFetchingNextPage}
                      onClick={() => void detail.fetchNextPage()}
                    >
                      {detail.isFetchingNextPage
                        ? t("library.loadingMore")
                        : t("library.loadMore")}
                    </button>
                  )}
                </>
              ) : (
                <AlbumEmpty
                  title={t(
                    hasActiveAlbumFilters
                      ? "albums.noMatchingItems"
                      : "albums.empty",
                  )}
                  action={
                    album?.accessRole === "owner" &&
                    !hasActiveAlbumFilters ? (
                      <button
                        className="primary"
                        type="button"
                        onClick={() => setAddingPhotos(true)}
                      >
                        <ImagePlus size={17} /> {t("albums.addPhotos")}
                      </button>
                    ) : undefined
                  }
                />
              )}
            </section>
          )}
        </div>
      </section>

      {viewerItem && (
        <PhotoViewer
          key={viewerItem.id}
          item={viewerItem}
          previousItem={viewerIndex > 0 ? items[viewerIndex - 1] : undefined}
          nextItem={
            viewerIndex < items.length - 1 ? items[viewerIndex + 1] : undefined
          }
          position={viewerIndex >= 0 ? viewerIndex + 1 : 1}
          loadedCount={viewerIndex >= 0 ? items.length : 1}
          onClose={() => {
            dismissViewerOverlay();
          }}
          onPrevious={() => {
            if (viewerIndex > 0) navigateViewer(items[viewerIndex - 1].id);
          }}
          onNext={() => {
            if (viewerIndex < items.length - 1)
              navigateViewer(items[viewerIndex + 1].id);
          }}
          detailsOpen={viewerInfoOpen}
          onShowDetails={() => setViewerInfoOpen((open) => !open)}
          onFavorite={() => void favorite(viewerItem)}
          onShare={() => setSharingItems([viewerItem])}
          onRename={() => setRenamingItem(viewerItem)}
          onTrash={() =>
            setPendingTrash(
              viewerItem.capabilities.canTrash ? [viewerItem] : [],
            )
          }
          detailsPanel={
            viewerInfoOpen ? (
              <ItemDetails
                mode="viewer-info"
                item={viewerItem}
                busy={busy}
                onClose={() => setViewerInfoOpen(false)}
                onRename={() => undefined}
                onFavorite={() => undefined}
                onMove={() => undefined}
                folderOptions={[]}
                onTrash={() => undefined}
                onRestore={() => undefined}
                onRetryMetadata={() =>
                  void mutateItems({
                    source: [viewerItem],
                    eligible: (item) =>
                      Boolean(item.capabilities.canRetryMetadata) &&
                      item.metadata?.state === "failed",
                    action: (item) => retryMetadata(item.id),
                    successLabel: t("albums.queuedMetadata"),
                    clearAfter: false,
                  })
                }
                onShare={() => undefined}
                showMoveAction={false}
              />
            ) : undefined
          }
        />
      )}
      {renamingItem && (
        <RenameItemDialog
          item={renamingItem}
          busy={busy}
          onSave={(name) => renameViewerItem(renamingItem, name)}
          onClose={() => setRenamingItem(null)}
        />
      )}
      {pendingTrash.length > 0 && (
        <TrashPreviewDialog
          items={pendingTrash}
          busy={busy}
          onClose={() => setPendingTrash([])}
          onConfirm={async () => {
            await mutateItems({
              source: pendingTrash,
              eligible: (item) => item.capabilities.canTrash && !item.trashedAt,
              action: (item) => trashItem(item.id),
              successLabel: t("albums.movedTrash"),
            });
            setPendingTrash([]);
            setViewerInfoOpen(false);
            dismissViewerOverlay();
          }}
        />
      )}
      {pendingAlbumDownload && (
        <DownloadConfirmationDialog
          request={{
            kind: "album",
            id: pendingAlbumDownload.id,
            name: pendingAlbumDownload.name,
          }}
          onConfirm={confirmAlbumDownload}
          onClose={() => setPendingAlbumDownload(null)}
        />
      )}
      {sharingItems.length > 0 && (
        <ShareDialog items={sharingItems} onClose={() => setSharingItems([])} />
      )}
      {addingItems.length > 0 && (
        <AddToAlbumDialog
          items={addingItems}
          onClose={() => setAddingItems([])}
        />
      )}
      {addingPhotos && album?.accessRole === "owner" && (
        <AddPhotosToAlbumDialog
          album={album}
          existingIds={items.map((item) => item.id)}
          onClose={() => setAddingPhotos(false)}
          onAdded={(count, unchanged) =>
            setMessage(
              t("albums.batchAdded", {
                count,
                unchanged,
              }),
            )
          }
          onBrowsePhotos={() => {
            setAddingPhotos(false);
            router.push("/photos");
          }}
        />
      )}
      {editing && (
        <AlbumEditorDialog
          album={editing === "new" ? undefined : editing}
          onClose={() => setEditing(null)}
        />
      )}
      {sharing && (
        <AlbumShareDialog album={sharing} onClose={() => setSharing(null)} />
      )}
      {contextMenu && (
        <CatalogItemContextMenu
          state={contextMenu}
          busy={busy}
          onClose={() => setContextMenu(null)}
          onOpen={() => openViewer(contextMenu.item)}
          openInNewTabHref={catalogPreviewHref(
            pathname,
            searchParams.toString(),
            contextMenu.item.id,
          )}
          onDownload={() => download([contextMenu.item])}
          onFavorite={() => void favorite(contextMenu.item)}
          onDetails={() => {
            openViewer(contextMenu.item);
            setViewerInfoOpen(true);
          }}
          onRename={() => setRenamingItem(contextMenu.item)}
          onShare={() => setSharingItems([contextMenu.item])}
          onTrash={() => setPendingTrash([contextMenu.item])}
          extraActions={
            album?.accessRole === "owner"
              ? [
                  {
                    label: t("albums.remove"),
                    icon: <ImageMinus />,
                    onClick: () => void remove([contextMenu.item]),
                  },
                ]
              : undefined
          }
        />
      )}
      {shortcutsOpen && (
        <CatalogKeyboardShortcutsDialog
          onClose={() => setShortcutsOpen(false)}
        />
      )}
    </main>
  );
}

function AlbumFilterBar({
  mediaType,
  onMediaTypeChange,
  photoSort,
  onPhotoSortChange,
  years,
  year,
  onYearChange,
  tags,
  selectedTagId,
  onSelectedTagChange,
  favoritesOnly,
  onToggleFavorites,
  hasActiveFilters,
  onReset,
}: {
  mediaType: MediaTypeFilter;
  onMediaTypeChange: (value: MediaTypeFilter) => void;
  photoSort: PhotoSort;
  onPhotoSortChange: (value: PhotoSort) => void;
  years: number[];
  year?: number;
  onYearChange: (value?: number) => void;
  tags: CatalogTag[];
  selectedTagId: string;
  onSelectedTagChange: (value: string) => void;
  favoritesOnly: boolean;
  onToggleFavorites: () => void;
  hasActiveFilters: boolean;
  onReset: () => void;
}) {
  const { t } = usePreferences();
  const sortLabel =
    photoSort === "captured_desc"
      ? t("library.newestFirst")
      : photoSort === "captured_asc"
        ? t("library.oldestFirst")
        : t("library.recentlyAdded");
  const typeLabel =
    mediaType === "image"
      ? t("library.photos")
      : mediaType === "video"
        ? t("library.videos")
        : t("library.all");
  const selectedTag = tags.find((tag) => tag.id === selectedTagId);
  const selectableYears =
    year !== undefined && !years.includes(year)
      ? [year, ...years].sort((left, right) => right - left)
      : years;

  return (
    <>
      <div className="photo-filter-row album-filter-row" aria-label={t("albums.filters")}>
        <label className="filter-select">
          <span>{t("library.type")}</span>
          <select
            value={mediaType}
            onChange={(event) =>
              onMediaTypeChange(event.target.value as MediaTypeFilter)
            }
          >
            <option value="all">{t("library.all")}</option>
            <option value="image">{t("library.photos")}</option>
            <option value="video">{t("library.videos")}</option>
          </select>
        </label>
        <label className="filter-select">
          <span>{t("library.sort")}</span>
          <select
            value={photoSort}
            onChange={(event) =>
              onPhotoSortChange(event.target.value as PhotoSort)
            }
          >
            <option value="added_desc">{t("library.recentlyAdded")}</option>
            <option value="captured_desc">{t("library.newestFirst")}</option>
            <option value="captured_asc">{t("library.oldestFirst")}</option>
          </select>
        </label>
        {selectableYears.length > 0 && (
          <label className="filter-select">
            <span>{t("library.year")}</span>
            <select
              value={year ?? ""}
              onChange={(event) =>
                onYearChange(
                  event.target.value ? Number(event.target.value) : undefined,
                )
              }
            >
              <option value="">{t("library.allYears")}</option>
              {selectableYears.map((candidate) => (
                <option key={candidate} value={candidate}>
                  {candidate}
                </option>
              ))}
            </select>
          </label>
        )}
        {tags.length > 0 && (
          <label className="filter-select tag-filter-select">
            <span>{t("library.tag")}</span>
            <select
              value={selectedTagId}
              onChange={(event) => onSelectedTagChange(event.target.value)}
            >
              <option value="">{t("library.allTags")}</option>
              {tags.map((tag) => (
                <option key={tag.id} value={tag.id}>
                  {tag.name}
                </option>
              ))}
            </select>
          </label>
        )}
        <button
          className={`favorite-filter ${favoritesOnly ? "active" : ""}`}
          type="button"
          aria-pressed={favoritesOnly}
          aria-label={t("library.showFavoritesOnly")}
          title={t("library.showFavoritesOnly")}
          onClick={onToggleFavorites}
        >
          <Heart fill={favoritesOnly ? "currentColor" : "none"} />
          <span>{t("library.favorites")}</span>
        </button>
        {hasActiveFilters && (
          <button className="filter-reset" type="button" onClick={onReset}>
            <RotateCcw size={14} /> {t("library.reset")}
          </button>
        )}
      </div>
      {(mediaType !== "all" ||
        photoSort !== "added_desc" ||
        year !== undefined ||
        favoritesOnly ||
        selectedTagId) && (
        <div className="active-filter-chips" aria-label={t("library.activeFilters")}>
          {mediaType !== "all" && (
            <button type="button" onClick={() => onMediaTypeChange("all")}>
              {t("library.type")}: {typeLabel} <X />
            </button>
          )}
          {photoSort !== "added_desc" && (
            <button
              type="button"
              onClick={() => onPhotoSortChange("added_desc")}
            >
              {t("library.sort")}: {sortLabel} <X />
            </button>
          )}
          {year !== undefined && (
            <button type="button" onClick={() => onYearChange()}>
              {t("library.year")}: {year} <X />
            </button>
          )}
          {favoritesOnly && (
            <button type="button" onClick={onToggleFavorites}>
              {t("library.favorites")} <X />
            </button>
          )}
          {selectedTagId && (
            <button type="button" onClick={() => onSelectedTagChange("")}>
              {t("library.tag")}: {selectedTag?.name ?? selectedTagId} <X />
            </button>
          )}
        </div>
      )}
    </>
  );
}

function AlbumCover({ album }: { album: Album }) {
  const coverIds = album.coverFileIds?.length
    ? album.coverFileIds
    : album.coverFileId
      ? [album.coverFileId]
      : [];
  const tiles =
    coverIds.length >= 3 ? coverIds.slice(0, 4) : coverIds.slice(0, 1);
  if (!tiles.length)
    return (
      <span className="album-cover album-cover-empty">
        <ImageOff />
      </span>
    );
  return (
    <span
      className={`album-cover${tiles.length >= 3 ? " album-cover-mosaic" : ""}`}
    >
      {tiles.map((fileId) => (
        <img
          key={fileId}
          src={thumbnailUrl(fileId, "md")}
          crossOrigin="use-credentials"
          alt=""
        />
      ))}
    </span>
  );
}

function AlbumEmpty({
  title,
  action,
}: {
  title: string;
  action?: React.ReactNode;
}) {
  return (
    <div className="album-empty">
      <AlbumIcon />
      <p>{title}</p>
      {action}
    </div>
  );
}
