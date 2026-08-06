"use client";

import { Cloud, Image as ImageIcon, Search } from "lucide-react";
import type { MouseEvent } from "react";
import { usePreferences } from "@/lib/preferences";
import type {
  LibraryView,
  MediaItem,
  PhotoSort,
  PhotoTimelineAnchor,
  TimelineEvent,
} from "@/lib/types";
import { FileListRow } from "./file-list-row";
import { LibraryEmptyState } from "./library-view-primitives";
import { MediaCard } from "./media-card";
import { PhotoTimeline } from "./photo-timeline";
import { PhotoTimelineScrubber } from "./photo-timeline-scrubber";

export type LibraryFileViewMode = "grid" | "list";

export function LibraryCatalogResults({
  view,
  items,
  timelineItems,
  selectedIds,
  fileViewMode,
  query,
  pending,
  errorMessage,
  onRetry,
  onSelect,
  onKeyboardRange,
  onSelectAll,
  onOpenItem,
  onOpenPhoto,
  onContextMenu,
  hasNextPage,
  loadingNextPage,
  onLoadNextPage,
  hasPreviousPage,
  loadingPreviousPage,
  onLoadPreviousPage,
  seekTargetKey,
  seekRequestId,
  onVisibleMonthChange,
  timelineAnchors,
  activeTimelineMonthKey,
  onSeekTimeline,
  timelineEvents = [],
  timelineSort,
  onSelectTimelineEvent,
  onEditTimelineEvent,
  selectedTimelineEvent,
}: {
  view: LibraryView;
  items: MediaItem[];
  timelineItems: MediaItem[];
  selectedIds: string[];
  fileViewMode: LibraryFileViewMode;
  query: string;
  pending: boolean;
  errorMessage?: string;
  onRetry: () => void;
  onSelect: (item: MediaItem, range: boolean) => void;
  onKeyboardRange: (itemId: string) => void;
  onSelectAll: () => void;
  onOpenItem: (item: MediaItem) => void;
  onOpenPhoto: (item: MediaItem) => void;
  onContextMenu: (event: MouseEvent<HTMLElement>, item: MediaItem) => void;
  hasNextPage?: boolean;
  loadingNextPage: boolean;
  onLoadNextPage: () => Promise<unknown>;
  hasPreviousPage?: boolean;
  loadingPreviousPage: boolean;
  onLoadPreviousPage: () => Promise<unknown>;
  seekTargetKey?: string;
  seekRequestId?: number;
  onVisibleMonthChange: (monthKey: string) => void;
  timelineAnchors: PhotoTimelineAnchor[];
  activeTimelineMonthKey?: string;
  onSeekTimeline: (anchor: PhotoTimelineAnchor) => void;
  timelineEvents?: TimelineEvent[];
  timelineSort: PhotoSort;
  onSelectTimelineEvent?: (event: TimelineEvent) => void;
  onEditTimelineEvent?: (event: TimelineEvent) => void;
  selectedTimelineEvent?: TimelineEvent | null;
}) {
  const { t } = usePreferences();

  if (pending)
    return (
      <LibraryEmptyState
        icon={<Cloud className="catalog-spinner" />}
        title={t("library.loading")}
        copy={t("library.readingCatalog")}
      />
    );

  if (errorMessage)
    return (
      <LibraryEmptyState
        icon={<Cloud />}
        title={t("library.loadFailed")}
        copy={errorMessage}
        action={onRetry}
      />
    );

  if (!items.length && !(view === "photos" && selectedTimelineEvent))
    return (
      <LibraryEmptyState
        icon={view === "photos" ? <ImageIcon /> : <Search />}
        title={
          query
            ? t("library.noMatches")
            : view === "photos"
              ? t("library.noMedia")
              : t("library.nothingHere")
        }
        copy={
          query
            ? t("library.clearFilterHint")
            : view === "shared"
              ? t("library.sharedEmpty")
              : t("library.emptyHint")
        }
      />
    );

  return (
    <>
      {view === "photos" ? (
        <div className="photo-timeline-layout">
          <PhotoTimeline
            items={timelineItems}
            selectedIds={selectedIds}
            selectionMode={selectedIds.length > 0}
            onSelect={onSelect}
            onKeyboardRange={onKeyboardRange}
            onSelectAll={onSelectAll}
            onOpen={onOpenPhoto}
            onContextMenu={onContextMenu}
            hasMore={Boolean(hasNextPage)}
            loadingMore={loadingNextPage}
            onLoadMore={onLoadNextPage}
            hasPrevious={Boolean(hasPreviousPage)}
            loadingPrevious={loadingPreviousPage}
            onLoadPrevious={onLoadPreviousPage}
            seekTargetKey={seekTargetKey}
            seekRequestId={seekRequestId}
            onVisibleMonthChange={onVisibleMonthChange}
            events={timelineEvents}
            sort={timelineSort}
            selectedEventId={selectedTimelineEvent?.id}
            onSelectEvent={onSelectTimelineEvent}
            onEditEvent={onEditTimelineEvent}
          />
          {timelineAnchors.length > 1 && (
            <PhotoTimelineScrubber
              anchors={timelineAnchors}
              activeKey={activeTimelineMonthKey}
              activeEvent={selectedTimelineEvent ?? undefined}
              onSeek={onSeekTimeline}
              events={timelineEvents}
              onSelectEvent={onSelectTimelineEvent}
            />
          )}
        </div>
      ) : view === "files" && fileViewMode === "list" ? (
        <div className="file-list" role="list">
          <div className="file-list-header" aria-hidden="true">
            <span />
            <span>{t("files.name")}</span>
            <span>{t("library.type")}</span>
            <span>{t("details.modified")}</span>
            <span>{t("files.size")}</span>
            <span />
          </div>
          {items.map((item) => (
            <FileListRow
              key={item.id}
              item={item}
              selected={selectedIds.includes(item.id)}
              onSelect={(range) => onSelect(item, Boolean(range))}
              onOpen={() => onOpenItem(item)}
              onContextMenu={(event) => onContextMenu(event, item)}
              onKeyboardRange={onKeyboardRange}
              onSelectAll={onSelectAll}
            />
          ))}
        </div>
      ) : (
        <div className="media-grid">
          {items.map((item) => (
            <MediaCard
              key={item.id}
              item={item}
              selected={selectedIds.includes(item.id)}
              selectionMode={selectedIds.length > 0}
              onSelect={(range) => onSelect(item, Boolean(range))}
              onKeyboardRange={onKeyboardRange}
              onSelectAll={onSelectAll}
              onOpen={() => onOpenItem(item)}
              onContextMenu={(event) => onContextMenu(event, item)}
            />
          ))}
        </div>
      )}
      {view !== "photos" && hasNextPage && (
        <button
          className="secondary load-more"
          disabled={loadingNextPage}
          onClick={onLoadNextPage}
        >
          {loadingNextPage ? t("library.loadingMore") : t("library.loadMore")}
        </button>
      )}
    </>
  );
}
