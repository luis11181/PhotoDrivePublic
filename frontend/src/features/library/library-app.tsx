"use client";

import { useQuery, useQueryClient } from "@tanstack/react-query";
import {
  Download,
  CalendarPlus,
  CalendarRange,
  FolderPlus,
  Keyboard,
  LayoutGrid,
  List,
  Trash2,
  Upload,
} from "lucide-react";
import { usePathname, useRouter, useSearchParams } from "next/navigation";
import {
  useCallback,
  useEffect,
  useMemo,
  useRef,
  useState,
  type DragEvent,
} from "react";
import type { CurrentUser } from "@/lib/auth";
import { logError, logInfo } from "@/lib/logger";
import {
  createFolder,
  createTimelineEvent,
  contentUrl,
  copyFile,
  downloadFolderUrl,
  downloadItemsUrl,
  getItem,
  getStorageSummary,
  isDemoMode,
  listFavoriteFolders,
  listPhotoTimelineAnchors,
  listPhotoYears,
  listTags,
  listTimelineEvents,
  moveItem,
  renameItem,
  retryMetadata,
  restoreItem,
  setFavorite,
  updateTimelineEvent,
  deleteTimelineEvent,
} from "@/lib/api";
import { items as demoItems } from "@/lib/demo-data";
import { usePreferences } from "@/lib/preferences";
import type {
  FileDiscoveryScope,
  LibraryView,
  FileTypeFilter,
  ItemSort,
  MediaItem,
  MediaTypeFilter,
  PhotoSort,
  PhotoScope,
  PhotoTimelineAnchor,
  TimelineEvent,
} from "@/lib/types";
import { LibrarySidebar } from "./components/library-sidebar";
import { LibraryTopbar } from "./components/library-topbar";
import { LibraryBreadcrumbs } from "./components/library-breadcrumbs";
import { anchorKey } from "./components/photo-timeline-scrubber";
import { SelectionActions } from "./components/selection-actions";
import { useLibraryQuery } from "./use-library-query";
import { useCatalogSearch } from "./use-catalog-search";
import { useOperationNotifications } from "./use-operation-notifications";
import { PhotoGridDensityControl } from "./components/library-view-primitives";
import { LibraryFilterBar } from "./components/library-filter-bar";
import {
  LibraryCatalogResults,
  type LibraryFileViewMode,
} from "./components/library-catalog-results";
import { useUploadQueue } from "@/features/uploads/upload-queue-provider";
import {
  catalogDetailsHref,
  catalogPreviewHref,
} from "@/features/catalog/catalog-item-links";
import { useCatalogOverlayNavigation } from "@/features/catalog/use-catalog-overlay-navigation";
import { useCatalogSelection } from "@/features/catalog/use-catalog-selection";
import { useCatalogContextMenu } from "@/features/catalog/use-catalog-context-menu";
import { useCatalogKeyboardShortcuts } from "@/features/catalog/use-catalog-keyboard-shortcuts";
import { useLibraryMutations } from "./use-library-mutations";
import { useLibraryTagActions } from "./use-library-tag-actions";
import { useLibraryCaptureDateActions } from "./use-library-capture-date-actions";
import { useLibraryTrashActions } from "./use-library-trash-actions";
import { LibraryItemOverlays } from "./components/library-item-overlays";
import { isPreviewable, previewKind } from "./preview-support";
import {
  LibraryDialogStack,
  type LibraryDialogName,
  type LibraryToastMessage,
} from "./components/library-dialog-stack";
import type { TimelineEventValues } from "./components/timeline-event-dialog";
import {
  DownloadConfirmationDialog,
  type DownloadConfirmationRequest,
} from "./components/download-confirmation-dialog";

type PendingLibraryDownload =
  | { kind: "items"; items: MediaItem[] }
  | { kind: "folder"; folderId: string; name: string };

export function LibraryApp({
  view,
  demoMode = false,
  onOpenAccount,
  onSignOut,
  user,
}: {
  view: LibraryView;
  demoMode?: boolean;
  onOpenAccount?: () => void;
  onSignOut?: () => Promise<void>;
  user?: CurrentUser;
}) {
  const router = useRouter();
  const pathname = usePathname();
  const searchParams = useSearchParams();
  const queryClient = useQueryClient();
  const {
    photoGridDensity,
    setPhotoGridDensity,
    showTimelineEvents,
    setShowTimelineEvents,
    t,
  } = usePreferences();
  const uploadQueue = useUploadQueue();
  const parentId =
    view === "files" || view === "shared"
      ? (searchParams.get("folder") ?? undefined)
      : undefined;
  const parentName =
    view === "files" || view === "shared"
      ? (searchParams.get("name") ?? undefined)
      : undefined;
  const parentRole =
    view === "files" ? (searchParams.get("role") ?? undefined) : undefined;
  const viewerItemId = searchParams.get("preview");
  const detailsItemId = searchParams.get("details");
  const overlayNavigation = useCatalogOverlayNavigation(
    pathname,
    searchParams.toString(),
  );
  const [query, setQuery] = useState("");
  const [photoScope, setPhotoScope] = useState<PhotoScope>("folder");
  const [mediaType, setMediaType] = useState<string>("all");
  const [fileType, setFileType] = useState<FileTypeFilter>("all");
  const [itemSortByView, setItemSortByView] = useState<
    Partial<Record<LibraryView, ItemSort>>
  >({ files: "modified_desc" });
  const itemSort = itemSortByView[view] ?? "name_asc";
  const setItemSort = (sort: ItemSort) =>
    setItemSortByView((current) => ({ ...current, [view]: sort }));
  const [photoSort, setPhotoSort] = useState<PhotoSort>("captured_desc");
  const [photoYear, setPhotoYear] = useState<number | undefined>();
  const [photoSeek, setPhotoSeek] = useState<{
    filterKey: string;
    monthKey: string;
    cursor: string;
    requestId: number;
  }>();
  const [visibleTimelineMonth, setVisibleTimelineMonth] = useState<{
    filterKey: string;
    monthKey: string;
  }>();
  const photoSeekSequence = useRef(0);
  const [photosFavorite, setPhotosFavorite] = useState(false);
  const [selectedTimelineEvent, setSelectedTimelineEvent] =
    useState<TimelineEvent | null>(null);
  const [timelineEventSeekRequestId, setTimelineEventSeekRequestId] =
    useState<number>();
  const [editingTimelineEvent, setEditingTimelineEvent] =
    useState<TimelineEvent | null>(null);
  const [filesFavorite, setFilesFavorite] = useState(false);
  const [fileSearchScope, setFileSearchScope] =
    useState<FileDiscoveryScope>("subtree");
  const [searchIncludesPhotos, setSearchIncludesPhotos] = useState(false);
  const [details, setDetails] = useState<MediaItem | null>(null);
  const [sharingItems, setSharingItems] = useState<MediaItem[]>([]);
  const [albumItems, setAlbumItems] = useState<MediaItem[]>([]);
  const [tagItems, setTagItemsSelection] = useState<MediaItem[]>([]);
  const [tagsManaging, setTagsManaging] = useState(false);
  const [captureDateItems, setCaptureDateItems] = useState<MediaItem[]>([]);
  const [renamingItem, setRenamingItem] = useState<MediaItem | null>(null);
  const [selectedTagId, setSelectedTagId] = useState("");
  const [viewerInfoOpen, setViewerInfoOpen] = useState(false);
  const previewOpenedHere = useRef(false);
  const [dialog, setDialog] = useState<LibraryDialogName>(null);
  const [mobileNavigation, setMobileNavigation] = useState(false);
  const [toast, setToast] = useState<LibraryToastMessage | null>(null);
  const toastTimeout = useRef<number | undefined>(undefined);
  const [fileViewMode, setFileViewMode] = useState<LibraryFileViewMode>("grid");
  const dragDepth = useRef(0);
  const [dropActive, setDropActive] = useState(false);
  const [uploadProgress, setUploadProgress] = useState("");
  const [notificationsOpen, setNotificationsOpen] = useState(false);
  const [pendingDownload, setPendingDownload] =
    useState<PendingLibraryDownload | null>(null);
  const normalizedQuery = query.trim();
  const photoCaptureDate =
    view === "photos" && /^\d{4}(?:-\d{2}(?:-\d{2})?)?$/.test(normalizedQuery)
      ? normalizedQuery
      : undefined;
  const effectiveFileSearchScope =
    fileSearchScope === "subtree"
      ? parentId
        ? "subtree"
        : "all"
      : fileSearchScope;
  const appliedTagId =
    view === "photos" || view === "files"
      ? selectedTagId || undefined
      : undefined;
  const searchTagId =
    view === "files" && effectiveFileSearchScope === "shared"
      ? undefined
      : appliedTagId;
  const fileDiscoveryActive =
    !demoMode &&
    !isDemoMode &&
    view === "files" &&
    (normalizedQuery.length >= 2 ||
      filesFavorite ||
      fileType !== "all" ||
      Boolean(appliedTagId));
  const catalogSearchActive =
    !demoMode &&
    !isDemoMode &&
    (fileDiscoveryActive ||
      (normalizedQuery.length >= 2 &&
        (view === "trash" || (view === "photos" && !photoCaptureDate))));
  const photoTimelineFilterKey = [
    photoScope,
    mediaType,
    photoSort,
    photosFavorite ? "favorite" : "all",
    photoCaptureDate ?? photoYear ?? "all-years",
    appliedTagId ?? "all-tags",
  ].join(":");
  const activePhotoSeek =
    photoSeek?.filterKey === photoTimelineFilterKey ? photoSeek : undefined;
  const catalogSearch = useCatalogSearch({
    query: normalizedQuery,
    scope:
      view === "photos"
        ? "photos"
        : view === "trash"
          ? "trash"
          : effectiveFileSearchScope,
    photoScope,
    parentId:
      view === "files" && effectiveFileSearchScope === "subtree"
        ? parentId
        : undefined,
    includePhotos:
      view === "files" &&
      effectiveFileSearchScope === "all" &&
      searchIncludesPhotos,
    type: view === "files" ? fileType : undefined,
    favorite: view === "files" ? filesFavorite : undefined,
    sort: view === "files" ? itemSort : undefined,
    tagId: searchTagId,
    enabled: catalogSearchActive,
  });
  const dataQuery = useLibraryQuery({
    view,
    parentId,
    photoScope,
    mediaType: view === "photos" ? (mediaType as MediaTypeFilter) : "all",
    fileType,
    enabled: !demoMode && !isDemoMode && !catalogSearchActive,
    photosFavorite,
    itemSort,
    photoSort,
    photoYear: photoCaptureDate ? undefined : photoYear,
    photoCaptureDate,
    photoSeekDate: selectedTimelineEvent?.startDate,
    photoSeekCursor: activePhotoSeek?.cursor,
    tagId: appliedTagId,
  });
  const tagsQuery = useQuery({
    queryKey: ["tags"],
    queryFn: listTags,
    enabled:
      !demoMode && !isDemoMode && (view === "files" || view === "photos"),
    staleTime: 30_000,
  });
  const photoYearsQuery = useQuery({
    queryKey: [
      "photo-years",
      photoScope,
      mediaType,
      photosFavorite,
      selectedTagId,
    ],
    queryFn: () =>
      listPhotoYears({
        scope: photoScope,
        type: mediaType as MediaTypeFilter,
        favorite: photosFavorite,
        tagId: selectedTagId || undefined,
      }),
    enabled: view === "photos" && !demoMode && !isDemoMode,
    staleTime: 60_000,
  });
  const photoTimelineAnchorsQuery = useQuery({
    queryKey: ["photo-timeline-anchors", photoTimelineFilterKey],
    queryFn: ({ signal }) =>
      listPhotoTimelineAnchors({
        signal,
        scope: photoScope,
        type: mediaType as MediaTypeFilter,
        favorite: photosFavorite,
        sort: photoSort as "captured_desc" | "captured_asc",
        year: photoCaptureDate ? undefined : photoYear,
        captureDate: photoCaptureDate,
        tagId: appliedTagId,
      }),
    enabled:
      view === "photos" &&
      !demoMode &&
      !isDemoMode &&
      !catalogSearchActive &&
      photoSort !== "added_desc",
    staleTime: 30_000,
  });
  const timelineEventsQuery = useQuery({
    queryKey: ["timeline-events"],
    queryFn: ({ signal }) => listTimelineEvents({ signal }),
    enabled: view === "photos" && !demoMode && !isDemoMode,
    staleTime: 30_000,
  });
  const eventSuggestionsQuery = useQuery({
    queryKey: ["timeline-event-suggestions", normalizedQuery],
    queryFn: ({ signal }) =>
      listTimelineEvents({ query: normalizedQuery, limit: 8, signal }),
    enabled:
      view === "photos" &&
      !demoMode &&
      !isDemoMode &&
      normalizedQuery.length >= 2 &&
      !photoCaptureDate,
    staleTime: 30_000,
  });
  const operationNotifications = useOperationNotifications();
  const quickAccessQuery = useQuery({
    queryKey: ["quick-access-folders"],
    queryFn: ({ signal }) => listFavoriteFolders({ signal }),
    enabled: !demoMode && !isDemoMode,
    staleTime: 30_000,
  });
  const storageSummaryQuery = useQuery({
    queryKey: ["storage-summary"],
    queryFn: () => getStorageSummary(),
    enabled: !demoMode && !isDemoMode,
    staleTime: Infinity,
    refetchOnMount: false,
    refetchOnWindowFocus: false,
  });

  const rawCatalogItems = useMemo(
    () =>
      demoMode || isDemoMode
        ? demoItems
        : ((catalogSearchActive
            ? catalogSearch
            : dataQuery
          ).data?.pages.flatMap((page) => page.items) ?? []),
    [catalogSearch, catalogSearchActive, dataQuery, demoMode],
  );
  const catalogItems = useMemo(
    () => uniqueCatalogItems(rawCatalogItems),
    [rawCatalogItems],
  );
  const duplicateCatalogItemCount =
    rawCatalogItems.length - catalogItems.length;
  const catalogViewerItem = catalogItems.find(
    (item) => item.id === viewerItemId,
  );
  const routedViewerQuery = useQuery({
    queryKey: ["catalog-item", viewerItemId],
    queryFn: () => getItem(viewerItemId!),
    enabled:
      Boolean(viewerItemId) && !catalogViewerItem && !demoMode && !isDemoMode,
    retry: false,
  });
  const routedDetailsQuery = useQuery({
    queryKey: ["catalog-item", detailsItemId],
    queryFn: () => getItem(detailsItemId!),
    enabled: Boolean(detailsItemId) && !demoMode && !isDemoMode,
    retry: false,
  });
  const breadcrumbsQuery = useQuery({
    queryKey: ["folder-breadcrumbs", view, parentId, parentName],
    queryFn: async () => {
      if (!parentId) return [];
      const folders: MediaItem[] = [];
      const visited = new Set<string>();
      let currentId: string | undefined = parentId;
      while (currentId && !visited.has(currentId) && folders.length < 64) {
        visited.add(currentId);
        const loaded = catalogItems.find((item) => item.id === currentId);
        const folder: MediaItem = loaded ?? (await getItem(currentId));
        if (folder.kind !== "folder") break;
        folders.unshift(folder);
        currentId = folder.parentId;
      }
      return folders;
    },
    enabled:
      Boolean(parentId) &&
      !demoMode &&
      !isDemoMode &&
      (view === "files" || view === "shared"),
    staleTime: 30_000,
  });

  useEffect(() => {
    const stored = window.localStorage.getItem("photodrive-files-view");
    if (stored !== "grid" && stored !== "list") return;
    const animationFrame = window.requestAnimationFrame(() =>
      setFileViewMode(stored),
    );
    return () => window.cancelAnimationFrame(animationFrame);
  }, []);

  useEffect(() => {
    if (!routedDetailsQuery.data) return;
    const animationFrame = window.requestAnimationFrame(() =>
      setDetails(routedDetailsQuery.data ?? null),
    );
    return () => window.cancelAnimationFrame(animationFrame);
  }, [routedDetailsQuery.data]);

  useEffect(() => {
    return () => {
      if (toastTimeout.current !== undefined)
        window.clearTimeout(toastTimeout.current);
    };
  }, []);

  useEffect(() => {
    if (duplicateCatalogItemCount <= 0) return;
    logInfo("frontend.catalog", "duplicate paged catalog items removed", {
      view,
      duplicateCount: duplicateCatalogItemCount,
    });
  }, [duplicateCatalogItemCount, view]);

  const visibleItems = useMemo(() => {
    const normalized = query.trim().toLowerCase();
    const isPhotoCaptureDate =
      view === "photos" && /^\d{4}(?:-\d{2}(?:-\d{2})?)?$/.test(normalized);
    return catalogItems.filter((item) => {
      if (
        normalized &&
        (!catalogSearchActive || normalized.length < 2) &&
        !isPhotoCaptureDate &&
        !item.name.toLowerCase().includes(normalized)
      )
        return false;
      if ((demoMode || isDemoMode) && view === "photos")
        return item.kind === "image" || item.kind === "video";
      if (view === "photos" && photosFavorite && !item.favorite) return false;
      if (view === "files" && filesFavorite && !item.favorite) return false;
      return true;
    });
  }, [
    catalogItems,
    catalogSearchActive,
    demoMode,
    filesFavorite,
    photosFavorite,
    query,
    view,
  ]);
  const {
    selectedIds,
    setSelectedIds,
    setLastSelectedId,
    selectedItems,
    allVisibleSelected,
    clearSelection,
    selectItem,
    selectItemById,
    toggleAllVisibleItems,
  } = useCatalogSelection({ items: catalogItems, visibleItems });
  const {
    busy,
    setBusy,
    mutateItem: mutate,
    mutateSelected,
  } = useLibraryMutations({
    demoMode,
    selectedItems,
    setDetails,
    setSelectedIds,
    setLastSelectedId,
    announce,
    operationNotifications,
    openNotifications: () => setNotificationsOpen(true),
  });
  const {
    createAndAssignTag,
    updateSelectedTag,
    updateManagedTag,
    deleteManagedTag,
    removeItemTag,
  } = useLibraryTagActions({
    tagItems,
    setTagItems: setTagItemsSelection,
    tags: tagsQuery.data ?? [],
    selectedTagId,
    setSelectedTagId,
    setDetails,
    setBusy,
    announce,
  });
  const { saveCaptureDate } = useLibraryCaptureDateActions({
    demoMode,
    items: captureDateItems,
    setSelectedIds,
    setLastSelectedId,
    clearSelection,
    setBusy,
    announce,
    operationNotifications,
    openNotifications: () => setNotificationsOpen(true),
  });
  const {
    pendingTrash,
    setPendingTrash,
    requestTrash,
    confirmTrash,
    trashSummaryQuery,
    clearTrashedItems,
  } = useLibraryTrashActions({
    view,
    demoMode,
    mutateItem: mutate,
    mutateSelected,
    announce,
    setBusy,
    clearSelection,
    operationNotifications,
    onDetailsTrashed: dismissDetailsOverlay,
    onViewerTrashed: (item) => {
      setViewerInfoOpen(false);
      dismissViewerOverlay();
      if (details?.id === item.id) dismissDetailsOverlay();
    },
    onTrashCleared: () => {
      setDetails(null);
      setDialog(null);
    },
  });
  const { contextMenu, setContextMenu, openContextMenu } =
    useCatalogContextMenu();
  const { shortcutsOpen, setShortcutsOpen, handleCatalogKeyDown } =
    useCatalogKeyboardShortcuts({
      disabled: Boolean(dialog || pendingTrash || viewerItemId),
      selectedItems,
      onSelectAll: toggleAllVisibleItems,
      onClearSelection: clearSelection,
      onOpen: openCatalogItem,
      onRename: setRenamingItem,
      onTrash: (item) => requestTrash([item], "selection"),
      onDetails: setDetails,
      onRestore: (item) =>
        void mutate(() => restoreItem(item.id), t("files.itemRestored")),
      onEscape: () => setContextMenu(null),
    });
  const activeDetails = useMemo(
    () =>
      details
        ? (catalogItems.find((item) => item.id === details.id) ?? details)
        : null,
    [catalogItems, details],
  );
  const timelineItems = visibleItems;
  const timelineAnchors =
    !catalogSearchActive && photoSort !== "added_desc"
      ? (photoTimelineAnchorsQuery.data?.anchors ?? [])
      : [];
  const timelineEvents = useMemo(() => {
    const events =
      showTimelineEvents &&
      !catalogSearchActive &&
      photoSort !== "added_desc"
        ? [...(timelineEventsQuery.data ?? [])]
        : [];
    if (
      selectedTimelineEvent &&
      !events.some((event) => event.id === selectedTimelineEvent.id)
    )
      events.push(selectedTimelineEvent);
    return events;
  }, [
    catalogSearchActive,
    photoSort,
    selectedTimelineEvent,
    showTimelineEvents,
    timelineEventsQuery.data,
  ]);
  const activeTimelineMonthKey =
    (visibleTimelineMonth?.filterKey === photoTimelineFilterKey
      ? visibleTimelineMonth.monthKey
      : undefined) ??
    selectedTimelineEvent?.startDate.slice(0, 7) ??
    activePhotoSeek?.monthKey ??
    (timelineAnchors[0] ? anchorKey(timelineAnchors[0]) : undefined);
  const handleVisibleTimelineMonth = useCallback(
    (monthKey: string) =>
      setVisibleTimelineMonth({ filterKey: photoTimelineFilterKey, monthKey }),
    [photoTimelineFilterKey],
  );
  const seekPhotoTimeline = useCallback(
    (anchor: PhotoTimelineAnchor) => {
      const monthKey = anchorKey(anchor);
      photoSeekSequence.current++;
      setSelectedTimelineEvent(null);
      setPhotoSeek({
        filterKey: photoTimelineFilterKey,
        monthKey,
        cursor: anchor.cursor,
        requestId: photoSeekSequence.current,
      });
      setVisibleTimelineMonth({ filterKey: photoTimelineFilterKey, monthKey });
      clearSelection();
    },
    [clearSelection, photoTimelineFilterKey, setSelectedTimelineEvent],
  );
  const selectTimelineEvent = useCallback(
    (timelineEvent: TimelineEvent) => {
      photoSeekSequence.current++;
      setSelectedTimelineEvent(timelineEvent);
      setTimelineEventSeekRequestId(photoSeekSequence.current);
      setQuery("");
      setPhotoYear(undefined);
      setPhotoSort("captured_desc");
      setPhotoSeek(undefined);
      clearSelection();
    },
    [
      clearSelection,
      setPhotoSeek,
      setPhotoSort,
      setPhotoYear,
      setQuery,
      setSelectedTimelineEvent,
    ],
  );
  const viewerItems = useMemo(
    () =>
      visibleItems.filter(
        (item) => item.kind === "image" || item.kind === "video",
      ),
    [visibleItems],
  );
  const hasActivePhotoFilters =
    photoScope !== "folder" ||
    mediaType !== "all" ||
    photoSort !== "captured_desc" ||
    photoYear !== undefined ||
    photosFavorite ||
    Boolean(selectedTagId);
  const activeQuery = catalogSearchActive ? catalogSearch : dataQuery;
  const viewerIndex = viewerItems.findIndex((item) => item.id === viewerItemId);
  const routedViewerItem = catalogViewerItem ?? routedViewerQuery.data;
  const viewerItem =
    viewerIndex >= 0
      ? viewerItems[viewerIndex]
      : routedViewerItem?.kind === "image" || routedViewerItem?.kind === "video"
        ? routedViewerItem
        : null;
  const routedPreviewKind = routedViewerItem
    ? previewKind(routedViewerItem)
    : undefined;
  const documentPreviewItem =
    routedViewerItem &&
    (routedPreviewKind === "audio" || routedPreviewKind === "text")
      ? routedViewerItem
      : null;
  const previewRoute =
    viewerItemId && !viewerItem && !documentPreviewItem
      ? {
          pending: !routedViewerItem && routedViewerQuery.isFetching,
          onRetry: routedViewerQuery.isError
            ? () => void routedViewerQuery.refetch()
            : undefined,
        }
      : null;
  const viewerPreviousItem =
    viewerIndex > 0 ? viewerItems[viewerIndex - 1] : undefined;
  const viewerNextItem =
    viewerIndex >= 0 && viewerIndex < viewerItems.length - 1
      ? viewerItems[viewerIndex + 1]
      : undefined;
  const hasNextPhotoPage = activeQuery.hasNextPage;
  const fetchingNextPhotoPage = activeQuery.isFetchingNextPage;
  const fetchNextPhotoPage = activeQuery.fetchNextPage;

  useEffect(() => {
    if (viewerItemId) return;
    previewOpenedHere.current = false;
  }, [viewerItemId]);

  useEffect(() => {
    if (
      view !== "photos" ||
      viewerIndex < 0 ||
      viewerItems.length - viewerIndex > 8 ||
      !hasNextPhotoPage ||
      fetchingNextPhotoPage
    )
      return;
    void fetchNextPhotoPage();
  }, [
    fetchNextPhotoPage,
    fetchingNextPhotoPage,
    hasNextPhotoPage,
    viewerItems.length,
    view,
    viewerIndex,
  ]);

  function announce(
    message: string,
    action?: Pick<LibraryToastMessage, "actionLabel" | "action">,
  ) {
    if (toastTimeout.current !== undefined)
      window.clearTimeout(toastTimeout.current);
    setToast({ message, ...action });
    toastTimeout.current = window.setTimeout(
      () => setToast(null),
      action?.action ? 7000 : 2600,
    );
  }

  async function runToastAction() {
    const action = toast?.action;
    if (!action) return;
    setToast(null);
    if (toastTimeout.current !== undefined)
      window.clearTimeout(toastTimeout.current);
    try {
      await action();
    } catch (reason) {
      announce(
        reason instanceof Error ? reason.message : t("files.undoFailed"),
      );
    }
  }

  function startItemsDownload(downloadable: MediaItem[], selectedCount: number) {
    const link = document.createElement("a");
    link.href = downloadItemsUrl(downloadable.map((item) => item.id));
    link.download =
      downloadable.length === 1
        ? downloadable[0].kind === "folder"
          ? `${downloadable[0].name}.zip`
          : downloadable[0].name
        : "";
    link.rel = "noopener";
    document.body.appendChild(link);
    link.click();
    link.remove();
    announce(
      downloadable.length === 1 && downloadable[0].kind !== "folder"
        ? `Downloading ${downloadable[0].name}`
        : `Preparing ${downloadable.length} items as a ZIP`,
    );
    logInfo("frontend.download", "download started", {
      selected: selectedCount,
      downloadable: downloadable.length,
      archive: downloadable.length > 1 || downloadable[0].kind === "folder",
    });
  }

  function downloadSelected(items: MediaItem[]) {
    if (demoMode || isDemoMode) {
      announce("Downloads require the backend");
      return;
    }
    const downloadable = items.filter((item) => !item.trashedAt);
    if (!downloadable.length) {
      announce("None of the selected items can be downloaded");
      return;
    }
    if (downloadable.length > 100) {
      announce("Select 100 or fewer items to download at once");
      return;
    }
    if (downloadable.length === 1 && downloadable[0].kind === "folder") {
      setPendingDownload({
        kind: "folder",
        folderId: downloadable[0].id,
        name: downloadable[0].name,
      });
      return;
    }
    if (downloadable.some((item) => item.kind === "folder")) {
      setPendingDownload({ kind: "items", items: downloadable });
      return;
    }
    startItemsDownload(downloadable, items.length);
  }

  function downloadFolder(folderId: string, name: string) {
    if (demoMode || isDemoMode) {
      announce(t("library.downloadRequiresBackend"));
      return;
    }
    setPendingDownload({ kind: "folder", folderId, name });
  }

  function confirmDownload() {
    if (!pendingDownload) return;
    if (pendingDownload.kind === "items") {
      startItemsDownload(pendingDownload.items, pendingDownload.items.length);
    } else {
      const link = document.createElement("a");
      link.href = downloadFolderUrl(pendingDownload.folderId);
      link.download = `${pendingDownload.name}.zip`;
      link.rel = "noopener";
      document.body.appendChild(link);
      link.click();
      link.remove();
      announce(
        t("library.preparingFolderZip", { name: pendingDownload.name }),
      );
      logInfo("frontend.download", "folder download started", {
        folderId: pendingDownload.folderId,
        archive: true,
      });
    }
    setPendingDownload(null);
  }

  const downloadConfirmationRequest: DownloadConfirmationRequest | null =
    pendingDownload?.kind === "items"
      ? { kind: "items", ids: pendingDownload.items.map((item) => item.id) }
      : pendingDownload?.kind === "folder"
        ? {
            kind: "folder",
            id: pendingDownload.folderId,
            name: pendingDownload.name,
          }
        : null;

  async function toggleFavoriteWithUndo(item: MediaItem) {
    const next = !item.favorite;
    const succeeded = await mutate(
      () => setFavorite(item.id, next),
      next ? t("files.addedFavorite") : t("files.removedFavorite"),
    );
    if (!succeeded) return;
    announce(next ? t("files.addedFavorite") : t("files.removedFavorite"), {
      actionLabel: t("files.undo"),
      action: async () => {
        await mutate(
          () => setFavorite(item.id, item.favorite),
          t("files.changeUndone"),
        );
      },
    });
  }

  async function renameWithUndo(item: MediaItem, name: string) {
    if (!name || name === item.name) return false;
    const succeeded = await mutate(
      () => renameItem(item.id, name),
      t("files.itemRenamed"),
    );
    if (!succeeded) return false;
    setRenamingItem(null);
    announce(t("files.itemRenamed"), {
      actionLabel: t("files.undo"),
      action: async () => {
        await mutate(
          () => renameItem(item.id, item.name),
          t("files.changeUndone"),
        );
      },
    });
    return true;
  }

  async function moveWithUndo(item: MediaItem, destination?: string) {
    const originalParentId = item.parentId;
    const succeeded = await mutate(
      () => moveItem(item.id, destination),
      t("files.itemMoved"),
      { title: t("files.moveFinished"), successLabel: t("files.moved") },
    );
    if (!succeeded) return;
    announce(t("files.itemMoved"), {
      actionLabel: t("files.undo"),
      action: async () => {
        await mutate(
          () => moveItem(item.id, originalParentId),
          t("files.changeUndone"),
        );
      },
    });
  }

  useEffect(() => {
    if (selectedIds.length === 0 || dialog || viewerItemId || pendingTrash)
      return;
    function clearOnEscape(event: KeyboardEvent) {
      if (
        event.key === "Escape" &&
        !document.querySelector('[data-selection-menu="open"]')
      ) {
        clearSelection();
      }
    }
    window.addEventListener("keydown", clearOnEscape);
    return () => window.removeEventListener("keydown", clearOnEscape);
  }, [clearSelection, dialog, pendingTrash, selectedIds.length, viewerItemId]);

  async function refreshStorageSummary() {
    try {
      await queryClient.fetchQuery({
        queryKey: ["storage-summary"],
        queryFn: () => getStorageSummary({ refresh: true }),
        staleTime: 0,
      });
    } catch (reason) {
      announce(
        reason instanceof Error
          ? reason.message
          : "Could not refresh storage usage",
      );
    }
  }

  function uploadOptions(allowNameConflict = false) {
    return {
      parentId,
      useDefaultMediaFolder: view === "photos" && !parentId,
      useDefaultDocumentsFolder: view === "files" && !parentId,
      allowNameConflict,
    };
  }

  async function chooseFiles(files: FileList) {
    if (demoMode || isDemoMode) {
      announce("Uploads require the backend");
      return;
    }
    const selectedFiles = Array.from(files);
    await uploadSelectedFiles(selectedFiles);
  }

  function changeFileViewMode(mode: LibraryFileViewMode) {
    setFileViewMode(mode);
    window.localStorage.setItem("photodrive-files-view", mode);
  }

  function handleDragEnter(event: DragEvent<HTMLElement>) {
    if (!canUpload || !event.dataTransfer.types.includes("Files")) return;
    event.preventDefault();
    dragDepth.current += 1;
    setDropActive(true);
  }

  function handleDragOver(event: DragEvent<HTMLElement>) {
    if (!canUpload || !event.dataTransfer.types.includes("Files")) return;
    event.preventDefault();
    event.dataTransfer.dropEffect = "copy";
  }

  function handleDragLeave(event: DragEvent<HTMLElement>) {
    if (!canUpload || !event.dataTransfer.types.includes("Files")) return;
    event.preventDefault();
    dragDepth.current = Math.max(0, dragDepth.current - 1);
    if (dragDepth.current === 0) setDropActive(false);
  }

  function handleDrop(event: DragEvent<HTMLElement>) {
    if (!canUpload) return;
    event.preventDefault();
    dragDepth.current = 0;
    setDropActive(false);
    const dropped = Array.from(event.dataTransfer.files);
    const accepted =
      view === "photos"
        ? dropped.filter(
            (file) =>
              file.type.startsWith("image/") || file.type.startsWith("video/"),
          )
        : dropped;
    const rejected = dropped.length - accepted.length;
    if (!accepted.length) {
      announce(t("files.dropMediaOnly"));
      return;
    }
    if (rejected > 0) announce(t("files.dropSkipped", { count: rejected }));
    void uploadSelectedFiles(accepted);
  }

  async function uploadSelectedFiles(
    selectedFiles: File[],
    allowNameConflict = false,
    skippedNameConflicts = 0,
  ) {
    setBusy(true);
    setUploadProgress(
      `Saving ${selectedFiles.length} ${selectedFiles.length === 1 ? "file" : "files"} to the durable queue…`,
    );
    logInfo("frontend.upload", "batch queueing started", {
      files: selectedFiles.length,
      view,
      parentId: parentId ?? null,
    });
    try {
      const result = selectedFiles.some((file) => file.webkitRelativePath)
        ? await enqueueFolderUpload(selectedFiles, allowNameConflict)
        : await uploadQueue.enqueueFiles(
            selectedFiles,
            uploadOptions(allowNameConflict),
          );
      if (result.accepted > 0) setDialog(null);
      logInfo("frontend.upload", "batch queued", {
        files: selectedFiles.length,
        accepted: result.accepted,
        failed: result.failed,
        skippedNameConflicts,
      });
      if (result.failed > 0) {
        operationNotifications.addResult({
          title: "Some files could not be queued",
          successLabel: "queued",
          succeeded: result.accepted,
          failed: result.failed,
          detail: result.error,
        });
        setNotificationsOpen(true);
      }
      announce(
        `${result.accepted} queued · ${result.failed} not saved${skippedNameConflicts ? ` · ${skippedNameConflicts} name conflicts skipped` : ""}`,
      );
    } catch (reason) {
      const detail =
        reason instanceof Error
          ? reason.message
          : "The selected folder could not be prepared for upload.";
      logError("frontend.upload", "batch queueing failed", {
        files: selectedFiles.length,
        view,
        parentId: parentId ?? null,
        reason: detail,
      });
      operationNotifications.addResult({
        title: "Folder upload could not be queued",
        successLabel: "queued",
        succeeded: 0,
        failed: selectedFiles.length,
        detail,
      });
      setNotificationsOpen(true);
      announce(detail);
    } finally {
      setBusy(false);
      setUploadProgress("");
    }
  }

  async function enqueueFolderUpload(
    selectedFiles: File[],
    allowNameConflict: boolean,
  ) {
    const directoryPaths = Array.from(
      new Set(
        selectedFiles.flatMap((file) => {
          const parts = file.webkitRelativePath.split("/").filter(Boolean);
          return parts
            .slice(0, -1)
            .map((_, index) => parts.slice(0, index + 1).join("/"));
        }),
      ),
    ).sort((left, right) => left.split("/").length - right.split("/").length);
    const folderIds = new Map<string, string | undefined>([["", parentId]]);
    for (const path of directoryPaths) {
      const parts = path.split("/");
      const folderName = parts.at(-1)!;
      const parentPath = parts.slice(0, -1).join("/");
      const folder = await createFolder(folderName, folderIds.get(parentPath));
      folderIds.set(path, folder.id);
    }

    let accepted = 0;
    let failed = 0;
    let error: string | undefined;
    const filesByDirectory = Map.groupBy(selectedFiles, (file) => {
      const parts = file.webkitRelativePath.split("/").filter(Boolean);
      return parts.slice(0, -1).join("/");
    });
    for (const [directory, files] of filesByDirectory) {
      const result = await uploadQueue.enqueueFiles(files, {
        ...uploadOptions(allowNameConflict),
        parentId: folderIds.get(directory),
        useDefaultDocumentsFolder: !folderIds.get(directory),
      });
      accepted += result.accepted;
      failed += result.failed;
      error ??= result.error;
    }
    await queryClient.invalidateQueries({ queryKey: ["library"] });
    return { accepted, failed, error };
  }

  async function makeCopy(item: MediaItem) {
    if (
      item.kind === "folder" ||
      item.accessRole === "viewer" ||
      item.trashedAt
    )
      return;
    await mutate(
      () => copyFile(item.id, item.parentId),
      t("files.copyCreated"),
      { title: t("files.copyFinished"), successLabel: t("files.copied") },
    );
  }

  async function createNewFolder(name: string) {
    if (!name || demoMode || isDemoMode) return;
    setBusy(true);
    logInfo("frontend.action", "create folder started", {
      parentId: parentId ?? null,
    });
    try {
      const folder = await createFolder(name, parentId);
      await queryClient.invalidateQueries({ queryKey: ["library"] });
      setDialog(null);
      logInfo("frontend.action", "create folder completed", {
        itemId: folder.id,
        parentId: folder.parentId ?? null,
      });
      operationNotifications.addResult({
        title: "Folder creation finished",
        successLabel: "created",
        succeeded: 1,
      });
      announce("Folder created");
    } catch (reason) {
      logError("frontend.action", "create folder failed", {
        parentId: parentId ?? null,
        reason: reason instanceof Error ? reason.message : "unknown_error",
      });
      operationNotifications.addResult({
        title: "Folder creation failed",
        successLabel: "created",
        succeeded: 0,
        failed: 1,
        detail:
          reason instanceof Error ? reason.message : "Could not create folder",
      });
      announce(
        reason instanceof Error
          ? reason.message
          : "Could not create the folder",
      );
    } finally {
      setBusy(false);
    }
  }

  async function saveTimelineEvent(values: TimelineEventValues) {
    setBusy(true);
    try {
      const saved = editingTimelineEvent
        ? await updateTimelineEvent(editingTimelineEvent.id, values)
        : await createTimelineEvent(values);
      if (selectedTimelineEvent?.id === saved.id) {
        photoSeekSequence.current++;
        setSelectedTimelineEvent(saved);
        setTimelineEventSeekRequestId(photoSeekSequence.current);
      }
      await queryClient.invalidateQueries({ queryKey: ["timeline-events"] });
      await queryClient.invalidateQueries({ queryKey: ["timeline-event-suggestions"] });
      announce(editingTimelineEvent ? t("events.updated") : t("events.created"));
    } finally {
      setBusy(false);
    }
  }

  async function removeTimelineEvent() {
    if (!editingTimelineEvent) return;
    setBusy(true);
    try {
      await deleteTimelineEvent(editingTimelineEvent.id);
      if (selectedTimelineEvent?.id === editingTimelineEvent.id)
        setSelectedTimelineEvent(null);
      await queryClient.invalidateQueries({ queryKey: ["timeline-events"] });
      await queryClient.invalidateQueries({ queryKey: ["timeline-event-suggestions"] });
      announce(t("events.deleted"));
    } finally {
      setBusy(false);
    }
  }

  function openItem(item: MediaItem) {
    if (item.kind === "folder" && view !== "trash") {
      router.push(folderItemHref(item, view));
      return;
    }
    if (previewKind(item) === "external" && !item.trashedAt) {
      window.open(contentUrl(item.id), "_blank", "noopener,noreferrer");
      return;
    }
    if (isPreviewable(item) && !item.trashedAt) {
      openPhoto(item);
      return;
    }
    setDetails(item);
  }

  function openCatalogItem(item: MediaItem) {
    openItem(item);
  }

  function itemHref(item: MediaItem) {
    if (item.kind === "folder" && !item.trashedAt)
      return folderItemHref(item, view);
    if (previewKind(item) === "external" && !item.trashedAt)
      return contentUrl(item.id);
    if (isPreviewable(item) && !item.trashedAt)
      return catalogPreviewHref(pathname, searchParams.toString(), item.id);
    return catalogDetailsHref(pathname, searchParams.toString(), item.id);
  }

  function openPhoto(item: MediaItem) {
    setViewerInfoOpen(false);
    if (viewerItemId) {
      overlayNavigation.replacePreview(item.id);
      return;
    }
    previewOpenedHere.current = true;
    overlayNavigation.pushPreview(item.id);
  }

  function navigateViewer(itemId: string) {
    overlayNavigation.replacePreview(itemId);
  }

  async function openNextViewerItem() {
    if (!viewerItem) return;
    if (viewerNextItem) {
      navigateViewer(viewerNextItem.id);
      return;
    }
    if (view !== "photos" || !hasNextPhotoPage) return;
    const result = await fetchNextPhotoPage();
    const loadedItems = uniqueCatalogItems(
      result.data?.pages.flatMap((page) => page.items) ?? [],
    );
    const currentIndex = loadedItems.findIndex(
      (item) => item.id === viewerItem.id,
    );
    const next = loadedItems[currentIndex + 1];
    if (next) navigateViewer(next.id);
  }

  function dismissViewerOverlay() {
    setViewerInfoOpen(false);
    if (!viewerItemId) return;
    if (previewOpenedHere.current) {
      previewOpenedHere.current = false;
      window.history.back();
      return;
    }
    overlayNavigation.replacePreview();
  }

  function dismissDetailsOverlay() {
    setDetails(null);
    if (detailsItemId) overlayNavigation.clearDetails();
  }

  const title =
    view === "photos"
      ? t("library.photos")
      : view === "files"
        ? parentRole === "documents_root"
          ? t("library.files")
          : (parentName ?? t("library.files"))
        : view === "shared"
          ? (parentName ?? t("library.shared"))
          : t("library.trash");
  const canUpload = view === "photos" || view === "files";

  return (
    <main
      className={`app-shell ${activeDetails ? "details-visible" : ""}`}
      onKeyDown={handleCatalogKeyDown}
    >
      <LibrarySidebar
        view={view}
        open={mobileNavigation}
        onClose={() => setMobileNavigation(false)}
        storageSummary={storageSummaryQuery.data}
        storageLoading={storageSummaryQuery.isLoading}
        storageRefreshing={storageSummaryQuery.isFetching}
        onRefreshStorage={() => void refreshStorageSummary()}
        isOwner={user?.isOwner ?? false}
        quickAccessFolders={quickAccessQuery.data?.items ?? []}
        quickAccessLoading={quickAccessQuery.isPending}
      />
      <section
        className={`workspace ${dropActive ? "drop-active" : ""}`}
        onDragEnter={handleDragEnter}
        onDragOver={handleDragOver}
        onDragLeave={handleDragLeave}
        onDrop={handleDrop}
      >
        <LibraryTopbar
          query={query}
          onQueryChange={(value) => {
            setQuery(value);
            if (value) setSelectedTimelineEvent(null);
            if (value.trim().length < 2 && fileSearchScope === "shared")
              setFileSearchScope("subtree");
          }}
          placeholder={
            view === "photos"
              ? t("events.searchPlaceholder")
              : view === "shared"
                ? t("library.sharedSearchDeferred")
                : effectiveFileSearchScope === "subtree" && parentName
                  ? t("library.searchInFolder", { name: parentName })
                  : t("library.searchFiles")
          }
          searchDisabled={view === "shared"}
          onOpenNavigation={() => setMobileNavigation(true)}
          onOpenAccount={onOpenAccount}
          onSignOut={onSignOut}
          onToggleNotifications={() =>
            setNotificationsOpen((current) => !current)
          }
          notificationCount={operationNotifications.notifications.length}
          notificationsOpen={notificationsOpen}
          operationRunning={operationNotifications.notifications.some(
            (notification) => notification.status === "running",
          )}
          user={user}
          eventSuggestions={view === "photos" ? (eventSuggestionsQuery.data ?? []) : []}
          eventSuggestionsLoading={view === "photos" && eventSuggestionsQuery.isFetching}
          onSelectEvent={view === "photos" ? selectTimelineEvent : undefined}
        />
        <div className="content">
          {(view === "files" || view === "shared") && (
            <LibraryBreadcrumbs
              view={view}
              folders={
                breadcrumbsQuery.data ??
                (parentId && parentName
                  ? [
                      {
                        id: parentId,
                        name: parentName,
                        kind: "folder",
                        systemRole:
                          parentRole === "documents_root"
                            ? "documents_root"
                            : undefined,
                      } as MediaItem,
                    ]
                  : [])
              }
              onRoot={() =>
                router.push(view === "files" ? "/files" : "/shared")
              }
              onFolder={openItem}
            />
          )}
          <div className="title-row">
            <div>
              <p className="eyebrow">{t("library.privateLibrary")}</p>
              <h1>{title}</h1>
            </div>
            {(canUpload ||
              view === "trash" ||
              (view === "shared" && parentId)) && (
              <div className="primary-actions">
                {(view === "files" || view === "shared") && parentId && (
                  <button
                    className="secondary"
                    onClick={() =>
                      downloadFolder(
                        parentId,
                        parentName ?? t("details.folder"),
                      )
                    }
                  >
                    <Download size={17} /> {t("library.downloadFolderZip")}
                  </button>
                )}
                {view === "files" && (
                  <button
                    className="secondary"
                    onClick={() => setDialog("folder")}
                  >
                    <FolderPlus size={17} /> {t("library.newFolder")}
                  </button>
                )}
                {view === "photos" && !demoMode && !isDemoMode && (
                  <button
                    className="secondary"
                    onClick={() => {
                      setEditingTimelineEvent(null);
                      setDialog("event");
                    }}
                  >
                    <CalendarPlus size={17} /> {t("events.new")}
                  </button>
                )}
                {canUpload && (
                  <button
                    className="primary"
                    onClick={() => setDialog("upload")}
                  >
                    <Upload size={17} /> {t("library.upload")}
                  </button>
                )}
                {view === "trash" && (
                  <button
                    className="secondary destructive"
                    disabled={
                      busy ||
                      trashSummaryQuery.isPending ||
                      !trashSummaryQuery.data ||
                      trashSummaryQuery.data.fileCount +
                        trashSummaryQuery.data.folderCount ===
                        0
                    }
                    onClick={() => setDialog("clear-trash")}
                  >
                    <Trash2 size={17} />{" "}
                    {t("library.clearTrash", {
                      count:
                        (trashSummaryQuery.data?.fileCount ?? 0) +
                        (trashSummaryQuery.data?.folderCount ?? 0),
                    })}
                  </button>
                )}
              </div>
            )}
          </div>

          <LibraryFilterBar
            view={view}
            normalizedQuery={normalizedQuery}
            parentId={parentId}
            photoScope={photoScope}
            onPhotoScopeChange={setPhotoScope}
            mediaType={mediaType}
            onMediaTypeChange={setMediaType}
            photoSort={photoSort}
            onPhotoSortChange={setPhotoSort}
            photoYears={photoYearsQuery.data}
            photoCaptureDate={photoCaptureDate}
            photoYear={photoYear}
            onPhotoYearChange={setPhotoYear}
            tags={tagsQuery.data ?? []}
            selectedTagId={selectedTagId}
            onSelectedTagChange={setSelectedTagId}
            onManageTags={() => setTagsManaging(true)}
            photosFavorite={photosFavorite}
            onTogglePhotosFavorite={() =>
              setPhotosFavorite((current) => !current)
            }
            filesFavorite={filesFavorite}
            onToggleFilesFavorite={() => {
              if (fileSearchScope === "shared") setFileSearchScope("subtree");
              setFilesFavorite((current) => !current);
            }}
            fileDiscoveryActive={fileDiscoveryActive}
            hasActivePhotoFilters={hasActivePhotoFilters}
            onResetPhotoFilters={() => {
              setPhotoScope("folder");
              setMediaType("all");
              setPhotoSort("captured_desc");
              setPhotoYear(undefined);
              setPhotosFavorite(false);
              setSelectedTagId("");
              setSelectedTimelineEvent(null);
            }}
            fileType={fileType}
            onFileTypeChange={setFileType}
            itemSort={itemSort}
            onItemSortChange={setItemSort}
            effectiveFileSearchScope={effectiveFileSearchScope}
            onFileSearchScopeChange={(scope) => {
              setFileSearchScope(scope);
              if (scope === "shared") {
                setSearchIncludesPhotos(false);
                setSelectedTagId("");
                setFilesFavorite(false);
              }
              if (scope === "subtree") setSearchIncludesPhotos(false);
            }}
            searchIncludesPhotos={searchIncludesPhotos}
            onSearchIncludesPhotosChange={setSearchIncludesPhotos}
          />
          <section className="library-section" aria-live="polite">
            <div className="section-heading">
              <div>
                <h2>
                  {catalogSearchActive
                    ? normalizedQuery.length >= 2
                      ? t("library.searchResults")
                      : t("library.filteredResults")
                    : view === "photos"
                      ? t("library.timeline")
                      : t("library.items")}
                </h2>
                <span className="item-count">
                  {t("library.loaded", { count: visibleItems.length })}
                </span>
              </div>
              {view === "photos" && (
                <div className="photo-heading-actions">
                  <button
                    type="button"
                    className={`timeline-events-toggle ${showTimelineEvents ? "active" : ""}`}
                    aria-pressed={showTimelineEvents}
                    title={t("events.show")}
                    onClick={() => setShowTimelineEvents(!showTimelineEvents)}
                  >
                    <CalendarRange /> <span>{t("events.shortLabel")}</span>
                  </button>
                  <PhotoGridDensityControl
                    value={photoGridDensity}
                    onChange={setPhotoGridDensity}
                  />
                </div>
              )}
              {view === "files" && (
                <div className="file-heading-actions">
                  <button
                    type="button"
                    className="file-shortcuts-button"
                    aria-label={t("files.keyboardShortcuts")}
                    title={t("files.keyboardShortcuts")}
                    onClick={() => setShortcutsOpen(true)}
                  >
                    <Keyboard />
                  </button>
                  <div
                    className="file-view-toggle"
                    role="group"
                    aria-label={t("files.viewMode")}
                  >
                    <button
                      type="button"
                      className={fileViewMode === "grid" ? "active" : undefined}
                      aria-pressed={fileViewMode === "grid"}
                      aria-label={t("files.gridView")}
                      title={t("files.gridView")}
                      onClick={() => changeFileViewMode("grid")}
                    >
                      <LayoutGrid />
                    </button>
                    <button
                      type="button"
                      className={fileViewMode === "list" ? "active" : undefined}
                      aria-pressed={fileViewMode === "list"}
                      aria-label={t("files.listView")}
                      title={t("files.listView")}
                      onClick={() => changeFileViewMode("list")}
                    >
                      <List />
                    </button>
                  </div>
                </div>
              )}
            </div>
            {selectedItems.length > 0 && (
              <SelectionActions
                selectedItems={selectedItems}
                folderOptions={catalogItems.filter(
                  (item) => item.kind === "folder" && !item.trashedAt,
                )}
                view={view}
                busy={busy}
                onOpen={(item) => {
                  openCatalogItem(item);
                  clearSelection();
                }}
                onFavorite={(favorite) =>
                  void mutateSelected({
                    title: "Updating favorites",
                    completedTitle: "Favorite update finished",
                    successLabel: favorite ? "favorited" : "unfavorited",
                    eligible: (item) =>
                      Boolean(item.capabilities.canFavorite) && !item.trashedAt,
                    batch: { action: "favorite", favorite },
                  })
                }
                onDownload={(items) => downloadSelected(items)}
                onMove={(destination) =>
                  void mutateSelected({
                    title: "Moving selected items",
                    completedTitle: "Move finished",
                    successLabel: "moved",
                    eligible: (item) =>
                      item.capabilities.canMove && !item.trashedAt,
                    batch: { action: "move", parentId: destination },
                  })
                }
                onTrash={() => requestTrash(selectedItems, "selection")}
                onRestore={() =>
                  void mutateSelected({
                    title: "Restoring selected items",
                    completedTitle: "Restore finished",
                    successLabel: "restored",
                    eligible: (item) => Boolean(item.trashedAt),
                    batch: { action: "restore" },
                  })
                }
                onRetryMetadata={() =>
                  void mutateSelected({
                    title: "Retrying selected media processing",
                    completedTitle: "Media processing retry finished",
                    successLabel: "queued",
                    eligible: (item) =>
                      !item.trashedAt &&
                      Boolean(item.capabilities.canRetryMetadata) &&
                      item.metadata?.state === "failed",
                    action: (item) => retryMetadata(item.id),
                  })
                }
                onCorrectCaptureDate={(items) => setCaptureDateItems(items)}
                onShare={(items) => setSharingItems(items)}
                onAddToAlbum={(items) => setAlbumItems(items)}
                onTags={(items) => setTagItemsSelection(items)}
                allVisibleSelected={allVisibleSelected}
                onSelectAll={toggleAllVisibleItems}
                onClear={clearSelection}
              />
            )}
            <LibraryCatalogResults
              view={view}
              items={visibleItems}
              timelineItems={timelineItems}
              selectedIds={selectedIds}
              fileViewMode={fileViewMode}
              query={query}
              hasActiveFilters={
                view === "photos"
                  ? hasActivePhotoFilters
                  : fileType !== "all" || filesFavorite || Boolean(selectedTagId) || searchIncludesPhotos
              }
              onClearFilters={() => {
                setQuery("");
                setPhotoScope("folder");
                setMediaType("all");
                setPhotoSort("captured_desc");
                setPhotoYear(undefined);
                setPhotosFavorite(false);
                setSelectedTimelineEvent(null);
                setFileType("all");
                setFilesFavorite(false);
                setSelectedTagId("");
                setSearchIncludesPhotos(false);
                setFileSearchScope("subtree");
              }}
              pending={activeQuery.isPending && !demoMode}
              errorMessage={
                activeQuery.isError ? activeQuery.error.message : undefined
              }
              onRetry={() => void activeQuery.refetch()}
              onSelect={selectItem}
              onKeyboardRange={selectItemById}
              onSelectAll={toggleAllVisibleItems}
              onOpenItem={openItem}
              onOpenPhoto={openPhoto}
              onContextMenu={openContextMenu}
              hasNextPage={activeQuery.hasNextPage}
              loadingNextPage={activeQuery.isFetchingNextPage}
              onLoadNextPage={() => activeQuery.fetchNextPage()}
              hasPreviousPage={activeQuery.hasPreviousPage}
              loadingPreviousPage={activeQuery.isFetchingPreviousPage}
              onLoadPreviousPage={() => activeQuery.fetchPreviousPage()}
              seekTargetKey={
                selectedTimelineEvent
                  ? `event:${selectedTimelineEvent.id}`
                  : activePhotoSeek?.monthKey
              }
              seekRequestId={
                selectedTimelineEvent
                  ? timelineEventSeekRequestId
                  : activePhotoSeek?.requestId
              }
              onVisibleMonthChange={handleVisibleTimelineMonth}
              timelineAnchors={timelineAnchors}
              activeTimelineMonthKey={activeTimelineMonthKey}
              onSeekTimeline={seekPhotoTimeline}
              timelineEvents={timelineEvents}
              timelineSort={photoSort}
              onSelectTimelineEvent={selectTimelineEvent}
              onEditTimelineEvent={(timelineEvent) => {
                setEditingTimelineEvent(timelineEvent);
                setDialog("event");
              }}
              selectedTimelineEvent={selectedTimelineEvent}
            />
          </section>
        </div>
        {dropActive && (
          <div className="upload-drop-overlay" aria-hidden="true">
            <Upload />
            <strong>
              {view === "photos" ? t("files.dropPhotos") : t("files.dropFiles")}
            </strong>
            <span>{t("files.dropQueueHelp")}</span>
          </div>
        )}
      </section>

      <LibraryItemOverlays
        view={view}
        busy={busy}
        details={activeDetails}
        folderOptions={catalogItems.filter((item) => item.kind === "folder")}
        onCloseDetails={dismissDetailsOverlay}
        onPreviewDetails={openItem}
        viewer={{
          item: viewerItem,
          previousItem: viewerPreviousItem,
          nextItem: viewerNextItem,
          hasMore: view === "photos" && Boolean(hasNextPhotoPage),
          position: viewerIndex >= 0 ? viewerIndex + 1 : 1,
          loadedCount: viewerIndex >= 0 ? viewerItems.length : 1,
          detailsOpen: viewerInfoOpen,
          onClose: dismissViewerOverlay,
          onPrevious: () => {
            if (viewerPreviousItem) navigateViewer(viewerPreviousItem.id);
          },
          onNext: () => void openNextViewerItem(),
          onToggleDetails: () => setViewerInfoOpen((open) => !open),
          onCloseDetails: () => setViewerInfoOpen(false),
          onRename: setRenamingItem,
        }}
        documentPreview={documentPreviewItem}
        previewRoute={previewRoute}
        actions={{
          onRename: (item, name) => void renameWithUndo(item, name),
          onFavorite: (item) => void toggleFavoriteWithUndo(item),
          onViewerFavorite: (item) =>
            void mutate(
              () => setFavorite(item.id, !item.favorite),
              item.favorite ? "Removed from favorites" : "Added to favorites",
            ),
          onMove: (item, destination) => void moveWithUndo(item, destination),
          onTrash: (item, source) => requestTrash([item], source),
          onRestore: (item) =>
            void mutate(() => restoreItem(item.id), "Item restored", {
              title: "Restore finished",
              successLabel: "restored",
            }),
          onRetryMetadata: (item) =>
            void mutate(
              () => retryMetadata(item.id),
              "Metadata retry finished",
            ),
          onCorrectCaptureDate: setCaptureDateItems,
          onShare: setSharingItems,
          onRemoveTag: (item, tagId) => void removeItemTag(item, tagId),
          onCopy: (item) => void makeCopy(item),
          onDownload: (item) => downloadSelected([item]),
        }}
      />
      {downloadConfirmationRequest && (
        <DownloadConfirmationDialog
          request={downloadConfirmationRequest}
          onConfirm={confirmDownload}
          onClose={() => setPendingDownload(null)}
        />
      )}
      <LibraryDialogStack
        view={view}
        busy={busy}
        uploadProgress={uploadProgress}
        tags={tagsQuery.data ?? []}
        trashSummary={trashSummaryQuery.data}
        sharingItems={sharingItems}
        setSharingItems={setSharingItems}
        albumItems={albumItems}
        setAlbumItems={setAlbumItems}
        tagItems={tagItems}
        setTagItems={setTagItemsSelection}
        tagsManaging={tagsManaging}
        setTagsManaging={setTagsManaging}
        captureDateItems={captureDateItems}
        setCaptureDateItems={setCaptureDateItems}
        renamingItem={renamingItem}
        setRenamingItem={setRenamingItem}
        pendingTrash={pendingTrash}
        setPendingTrash={setPendingTrash}
        dialog={dialog}
        setDialog={setDialog}
        contextMenu={contextMenu}
        setContextMenu={setContextMenu}
        shortcutsOpen={shortcutsOpen}
        setShortcutsOpen={setShortcutsOpen}
        notificationsOpen={notificationsOpen}
        setNotificationsOpen={setNotificationsOpen}
        notifications={operationNotifications.notifications}
        onDismissNotification={operationNotifications.dismiss}
        onClearNotifications={operationNotifications.clearCompleted}
        toast={toast}
        onToastAction={() => void runToastAction()}
        onCreateTag={createAndAssignTag}
        onSetTag={updateSelectedTag}
        onUpdateTag={updateManagedTag}
        onDeleteTag={deleteManagedTag}
        onSaveCaptureDate={saveCaptureDate}
        onRename={renameWithUndo}
        onConfirmTrash={confirmTrash}
        onChooseFiles={chooseFiles}
        onCreateFolder={createNewFolder}
        onClearTrash={clearTrashedItems}
        onOpenContextItem={openCatalogItem}
        contextItemHref={itemHref}
        onDownload={downloadSelected}
        onFavorite={(item) => void toggleFavoriteWithUndo(item)}
        onCopy={(item) => void makeCopy(item)}
        onDetails={setDetails}
        onShare={setSharingItems}
        onRequestTrash={(item) => requestTrash([item], "selection")}
        onRestore={(item) =>
          void mutate(() => restoreItem(item.id), t("files.itemRestored"), {
            title: t("files.restoreFinished"),
            successLabel: t("files.restored"),
          })
        }
        editingTimelineEvent={editingTimelineEvent}
        setEditingTimelineEvent={setEditingTimelineEvent}
        onSaveTimelineEvent={saveTimelineEvent}
        onDeleteTimelineEvent={removeTimelineEvent}
      />
    </main>
  );
}

function uniqueCatalogItems(items: MediaItem[]) {
  const seen = new Set<string>();
  return items.filter((item) => {
    if (seen.has(item.id)) return false;
    seen.add(item.id);
    return true;
  });
}

function folderItemHref(item: MediaItem, view: LibraryView) {
  const route =
    view === "shared" || item.accessRole === "viewer" ? "/shared" : "/files";
  const params = new URLSearchParams({ folder: item.id, name: item.name });
  if (item.systemRole) params.set("role", item.systemRole);
  return `${route}?${params.toString()}`;
}
